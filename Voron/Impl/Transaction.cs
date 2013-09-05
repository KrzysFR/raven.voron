﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Voron.Impl.FileHeaders;
using Voron.Impl.FreeSpace;
using Voron.Trees;

namespace Voron.Impl
{
	public class Transaction : IDisposable
	{
		public long NextPageNumber;

		private readonly IVirtualPager _pager;
		private readonly StorageEnvironment _env;
		private readonly long _id;

		private TreeDataInTransaction _rootTreeData;
		private Dictionary<Tuple<Tree, Slice>, Tree> _multiValueTrees;
		private readonly Dictionary<Tree, TreeDataInTransaction> _treesInfo = new Dictionary<Tree, TreeDataInTransaction>();
		private readonly Dictionary<long, long> _dirtyPages = new Dictionary<long, long>();
		private readonly List<long> _freedPages = new List<long>();
		private readonly HashSet<PagerState> _pagerStates = new HashSet<PagerState>();
		private readonly BinaryFreeSpaceStrategy freeSpaceHandling;

		public TransactionFlags Flags { get; private set; }

		public StorageEnvironment Environment
		{
			get { return _env; }
		}

		public IVirtualPager Pager
		{
			get { return _pager; }
		}

		public long Id
		{
			get { return _id; }
		}

		internal Action<long> AfterCommit = delegate { };
		private Dictionary<string, Tree> modifiedTrees;

		public Page TempPage
		{
			get { return _pager.TempPage; }
		}

		public Dictionary<string, Tree> ModifiedTrees
		{
			get { return modifiedTrees ?? (modifiedTrees = new Dictionary<string, Tree>(StringComparer.OrdinalIgnoreCase)); }
		}

		public bool Committed { get; private set; }

		public bool HasModifiedTrees
		{
			get { return modifiedTrees != null; }
		}

		public Transaction(IVirtualPager pager, StorageEnvironment env, long id, TransactionFlags flags, BinaryFreeSpaceStrategy freeSpaceHandling)
		{
			_pager = pager;
			_env = env;
			_id = id;
			this.freeSpaceHandling = freeSpaceHandling;
			Flags = flags;
			NextPageNumber = env.NextPageNumber;
		}

		public Page ModifyCursor(Tree tree, Cursor c)
		{
			var txInfo = GetTreeInformation(tree);
			return ModifyCursor(txInfo, c);
		}

		public Page ModifyCursor(TreeDataInTransaction txInfo, Cursor c)
		{
			Debug.Assert(c.Pages.Count > 0); // cannot modify an empty cursor

			var node = c.Pages.Last;
			while (node != null)
			{
				var parent = node.Next != null ? node.Next.Value : null;
				c.Update(node, ModifyPage(txInfo.Tree, parent, node.Value.PageNumber, c));
				node = node.Previous;
			}

			txInfo.RootPageNumber = c.Pages.Last.Value.PageNumber;

			return c.Pages.First.Value;
		}

		public unsafe Page ModifyPage(Tree tree, Page parent, long p, Cursor c)
		{
			long dirtyPageNum;
			Page page;
			if (_dirtyPages.TryGetValue(p, out dirtyPageNum))
			{
				page = c.GetPage(dirtyPageNum) ?? _pager.Get(this, dirtyPageNum);
				page.Dirty = true;
				UpdateParentPageNumber(parent, page.PageNumber);
				return page;
			}
			var newPage = AllocatePage(1);
			newPage.Dirty = true;
			var newPageNum = newPage.PageNumber;
			page = c.GetPage(p) ?? _pager.Get(this, p);
			NativeMethods.memcpy(newPage.Base, page.Base, _pager.PageSize);
			newPage.LastSearchPosition = page.LastSearchPosition;
			newPage.LastMatch = page.LastMatch;
			newPage.PageNumber = newPageNum;
			FreePage(p);
			_dirtyPages[p] = newPage.PageNumber;
			UpdateParentPageNumber(parent, newPage.PageNumber);
			return newPage;
		}

		private static unsafe void UpdateParentPageNumber(Page parent, long pageNumber)
		{
			if (parent == null)
				return;

			if (parent.Dirty == false)
				throw new InvalidOperationException("The parent page must already been dirtied, but wasn't");

			var node = parent.GetNode(parent.LastSearchPositionOrLastEntry);
			node->PageNumber = pageNumber;
		}

		public Page GetReadOnlyPage(long n)
		{
			long dirtyPage;
			if (_dirtyPages.TryGetValue(n, out dirtyPage))
				n = dirtyPage;
			return _pager.Get(this, n);
		}

		public Page AllocatePage(int num)
		{
			Page page = freeSpaceHandling.TryAllocateFromFreeSpace(this, num);
			if (page == null) // allocate from end of file
			{
				if (num > 1)
					_pager.EnsureContinuous(this, NextPageNumber, num);
				page = _pager.Get(this, NextPageNumber);
				page.PageNumber = NextPageNumber;
				NextPageNumber += num;
			}
			page.Lower = (ushort)Constants.PageHeaderSize;
			page.Upper = (ushort)_pager.PageSize;
			page.Dirty = true;
			_dirtyPages[page.PageNumber] = page.PageNumber;
			return page;
		}


		internal unsafe int GetNumberOfFreePages(NodeHeader* node)
		{
			return GetNodeDataSize(node) / Constants.PageNumberSize;
		}

		internal unsafe int GetNodeDataSize(NodeHeader* node)
		{
			if (node->Flags == (NodeFlags.PageRef)) // lots of data, enough to overflow!
			{
				var overflowPage = GetReadOnlyPage(node->PageNumber);
				return overflowPage.OverflowSize;
			}
			return node->DataSize;
		}

		public unsafe void Commit()
		{
			if (Flags != (TransactionFlags.ReadWrite))
				return; // nothing to do

			FlushAllMultiValues();

			foreach (var kvp in _treesInfo)
			{
				var txInfo = kvp.Value;
				var tree = kvp.Key;

				if (txInfo.RootPageNumber == tree.State.RootPageNumber &&
				    (modifiedTrees == null || modifiedTrees.ContainsKey(tree.Name) == false))
					continue; // not modified

				tree.DebugValidateTree(this, txInfo.RootPageNumber);
				txInfo.Flush();
				if (string.IsNullOrEmpty(kvp.Key.Name))
					continue;
		
				var treePtr = (TreeRootHeader*)_env.Root.DirectAdd(this, tree.Name, sizeof(TreeRootHeader));
				tree.State.CopyTo(treePtr);
			}

			freeSpaceHandling.RegisterFreePages(_freedPages);   // this is the the free space that is available when all concurrent transactions are done

			if (_rootTreeData != null)
			{
				_env.Root.DebugValidateTree(this, _rootTreeData.RootPageNumber);
				_rootTreeData.Flush();
			}

			_env.NextPageNumber = NextPageNumber;

			// Because we don't know in what order the OS will flush the pages 
			// we need to do this twice, once for the data, and then once for the metadata

			var sortedPagesToFlush = _dirtyPages.Select(x => x.Value).Distinct().ToList();
			sortedPagesToFlush.Sort();
			_pager.Flush(sortedPagesToFlush);

			freeSpaceHandling.OnCommit();

			_pager.Flush(freeSpaceHandling.GetBufferPages());

			WriteHeader(_pager.Get(this, _id & 1)); // this will cycle between the first and second pages

			_pager.Flush(_id & 1); // and now we flush the metadata as well

			_pager.Sync();

			Committed = true;

			AfterCommit(_id);
		}

		private unsafe void FlushAllMultiValues()
		{
			if (_multiValueTrees == null)
				return;

			foreach (var multiValueTree in _multiValueTrees)
			{
				var parentTree = multiValueTree.Key.Item1;
				var key = multiValueTree.Key.Item2;
				var childTree = multiValueTree.Value;

				TreeDataInTransaction value;
				if (_treesInfo.TryGetValue(childTree, out value) == false)
					continue;

				_treesInfo.Remove(childTree);
				var trh = (TreeRootHeader*)parentTree.DirectAdd(this, key, sizeof(TreeRootHeader));
				value.State.CopyTo(trh);

				parentTree.SetAsMultiValueTreeRef(this, key);
			}
		}

		private unsafe void WriteHeader(Page pg)
		{
			var fileHeader = (FileHeader*)pg.Base;
			fileHeader->TransactionId = _id;
			fileHeader->LastPageNumber = NextPageNumber - 1;
			freeSpaceHandling.CopyStateTo(&fileHeader->FreeSpace);
			_env.Root.State.CopyTo(&fileHeader->Root);
		}

		public void Dispose()
		{
			_env.TransactionCompleted(_id);
			foreach (var pagerState in _pagerStates)
			{
				pagerState.Release();
			}
		}

		public TreeDataInTransaction GetTreeInformation(Tree tree)
		{
			// ReSharper disable once PossibleUnintendedReferenceComparison
			if (tree == _env.Root)
			{
				return _rootTreeData ?? (_rootTreeData = new TreeDataInTransaction(_env.Root)
					{
						RootPageNumber = _env.Root.State.RootPageNumber
					});
			}

			TreeDataInTransaction c;
			if (_treesInfo.TryGetValue(tree, out c))
			{
				return c;
			}
			c = new TreeDataInTransaction(tree)
				{
					RootPageNumber = tree.State.RootPageNumber
				};
			_treesInfo.Add(tree, c);
			return c;
		}

		public void FreePage(long pageNumber)
		{
			_dirtyPages.Remove(pageNumber);
#if DEBUG
			Debug.Assert(pageNumber >= 2 && pageNumber <= _pager.NumberOfAllocatedPages);
			Debug.Assert(_freedPages.Contains(pageNumber) == false);
#endif
			_freedPages.Add(pageNumber);
		}

		internal void UpdateRoot(Tree root)
		{
			if (_treesInfo.TryGetValue(root, out _rootTreeData))
			{
				_treesInfo.Remove(root);
			}
			else
			{
				_rootTreeData = new TreeDataInTransaction(root);
			}
		}

		public void AddPagerState(PagerState state)
		{
			_pagerStates.Add(state);
		}

		public Cursor NewCursor(Tree tree)
		{
			return new Cursor();
		}

		public unsafe void AddMultiValueTree(Tree tree, Slice key, Tree mvTree)
		{
			if (_multiValueTrees == null)
				_multiValueTrees = new Dictionary<Tuple<Tree, Slice>, Tree>(new TreeAndSliceComparer(_env.SliceComparer));
			mvTree.IsMultiValueTree = true;
			_multiValueTrees.Add(Tuple.Create(tree, key), mvTree);
		}

		public bool TryGetMultiValueTree(Tree tree, Slice key, out Tree mvTree)
		{
			mvTree = null;
			if (_multiValueTrees == null)
				return false;
			return _multiValueTrees.TryGetValue(Tuple.Create(tree, key), out mvTree);
		}

		public bool TryRemoveMultiValueTree(Tree parentTree, Slice key)
		{
			var keyToRemove = Tuple.Create(parentTree, key);
			if (_multiValueTrees == null || !_multiValueTrees.ContainsKey(keyToRemove))
				return false;

			return _multiValueTrees.Remove(keyToRemove);
		}

	}

	internal unsafe class TreeAndSliceComparer : IEqualityComparer<Tuple<Tree, Slice>>
	{
		private readonly SliceComparer _comparer;

		public TreeAndSliceComparer(SliceComparer comparer)
		{
			_comparer = comparer;
		}

		public bool Equals(Tuple<Tree, Slice> x, Tuple<Tree, Slice> y)
		{
			if (x == null && y == null)
				return true;
			if (x == null || y == null)
				return false;

			if (x.Item1 != y.Item1)
				return false;

			return x.Item2.Compare(y.Item2, _comparer) == 0;
		}

		public int GetHashCode(Tuple<Tree, Slice> obj)
		{
			return obj.Item1.GetHashCode() ^ 397 * obj.Item2.GetHashCode();
		}
	}
}