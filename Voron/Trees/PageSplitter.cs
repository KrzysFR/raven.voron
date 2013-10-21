﻿using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Voron.Impl;

namespace Voron.Trees
{
    public unsafe class PageSplitter
    {
        private readonly Transaction _tx;
        private readonly SliceComparer _cmp;
        private readonly Slice _newKey;
        private readonly int _len;
        private readonly long _pageNumber;
	    private readonly NodeFlags _nodeType;
	    private readonly ushort _nodeVersion;
        private readonly Cursor _cursor;
        private readonly TreeDataInTransaction _txInfo;
        private readonly Page _page;
        private Page _parentPage;

        public PageSplitter(Transaction tx, SliceComparer cmp, Slice newKey, int len, long pageNumber, NodeFlags nodeType, ushort nodeVersion, Cursor cursor, TreeDataInTransaction txInfo)
        {
            _tx = tx;
            _cmp = cmp;
            _newKey = newKey;
            _len = len;
            _pageNumber = pageNumber;
	        _nodeType = nodeType;
	        _nodeVersion = nodeVersion;
            _cursor = cursor;
            _txInfo = txInfo;
            _page = _cursor.Pop();
        }

        public byte* Execute()
        {
            var rightPage = Tree.NewPage(_tx, _page.Flags);
            _txInfo.RecordNewPage(_page, 1);
            rightPage.Flags = _page.Flags;
            if (_cursor.PageCount == 0) // we need to do a root split
            {
                var newRootPage = Tree.NewPage(_tx, PageFlags.Branch);
                _cursor.Push(newRootPage);
                _txInfo.RootPageNumber = newRootPage.PageNumber;
                _txInfo.State.Depth++;
                _txInfo.RecordNewPage(newRootPage, 1);

                // now add implicit left page
				newRootPage.AddPageRefNode(0, Slice.BeforeAllKeys, _page.PageNumber);
                _parentPage = newRootPage;
                _parentPage.LastSearchPosition++;
                _parentPage.ItemCount = _page.ItemCount;
            }
            else
            {
                // we already popped the page, so the current one on the stack is what the parent of the page
                _parentPage = _cursor.CurrentPage;
            }

            if (_page.LastSearchPosition >= _page.NumberOfEntries)
            {
                // when we get a split at the end of the page, we take that as a hint that the user is doing 
                // sequential inserts, at that point, we are going to keep the current page as is and create a new 
                // page, this will allow us to do minimal amount of work to get the best density

                byte* pos;
                if (_page.IsBranch)
                {
                    // here we steal the last entry from the current page so we maintain the implicit null left entry
                    var node = _page.GetNode(_page.NumberOfEntries - 1);
                    Debug.Assert(node->Flags == NodeFlags.PageRef);
                    var itemsMoved = _tx.GetReadOnlyPage(node->PageNumber).ItemCount;
					rightPage.AddPageRefNode(0, Slice.Empty, node->PageNumber);
					pos = AddNodeToPage(rightPage, 1);
                    rightPage.ItemCount = itemsMoved;
    
                    AddSeparatorToParentPage(rightPage, new Slice(node));

                    _page.RemoveNode(_page.NumberOfEntries - 1);
                    _page.ItemCount -= itemsMoved;
                }
                else
                {
                    AddSeparatorToParentPage(rightPage, _newKey);
					pos = AddNodeToPage(rightPage, 0);
                }
                _cursor.Push(rightPage);
                IncrementItemCountIfNecessary();
                return pos;
            }

            return SplitPageInHalf(rightPage);
        }

		private byte* AddNodeToPage(Page page, int index)
		{
			switch (_nodeType)
			{
				case NodeFlags.PageRef:
					return page.AddPageRefNode(index, _newKey, _pageNumber);
				case NodeFlags.Data:
					return page.AddDataNode(index, _newKey, _len, _nodeVersion);
				case NodeFlags.MultiValuePageRef:
					return page.AddMultiValueNode(index, _newKey, _len, _nodeVersion);
				default:
					throw new NotSupportedException("Unknown node type");
			}
		}

        private byte* SplitPageInHalf(Page rightPage)
        {
            var currentIndex = _page.LastSearchPosition;
            var newPosition = true;
            var splitIndex = _page.NumberOfEntries / 2;
            if (currentIndex < splitIndex)
                newPosition = false;

            if (_page.IsLeaf)
            {
                splitIndex = AdjustSplitPosition(_newKey, _len, _page, currentIndex, splitIndex, ref newPosition);
            }

			var currentNode = _page.GetNode(splitIndex);
			var currentKey = new Slice(currentNode);

            // here we the current key is the separator key and can go either way, so 
            // use newPosition to decide if it stays on the left node or moves to the right
            Slice seperatorKey;
            if (currentIndex == splitIndex && newPosition)
            {
				seperatorKey = currentKey.Compare(_newKey, NativeMethods.memcmp) < 0 ? currentKey : _newKey;  
            }
            else
            {
	            seperatorKey = currentKey;
            }

            AddSeparatorToParentPage(rightPage, seperatorKey);

            // move the actual entries from page to right page
            var nKeys = _page.NumberOfEntries;
            for (int i = splitIndex; i < nKeys; i++)
            {
                var node = _page.GetNode(i);
                var itemsMoved = 1;
                if (_page.IsBranch && rightPage.NumberOfEntries == 0)
                {
                    rightPage.CopyNodeDataToEndOfPage(node, Slice.Empty);
                    itemsMoved = _tx.GetReadOnlyPage(node->PageNumber).ItemCount;
                }
                else
                {
                    rightPage.CopyNodeDataToEndOfPage(node);
                }
                rightPage.ItemCount += itemsMoved;
                _page.ItemCount -= itemsMoved;
            }
            _page.Truncate(_tx, splitIndex);

            byte* dataPos;
            // actually insert the new key
            return (currentIndex > splitIndex || newPosition && currentIndex == splitIndex)
                ? InsertNewKey(rightPage) : InsertNewKey(_page);
        }

        private byte* InsertNewKey(Page p)
        {
            var pos = p.NodePositionFor(_newKey, _cmp);
			var dataPos = AddNodeToPage(p, pos);
            _cursor.Push(p);
            IncrementItemCountIfNecessary();
            return dataPos;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void IncrementItemCountIfNecessary()
        {
            if (_len > -1)
                _cursor.IncrementItemCount();
        }

        private void AddSeparatorToParentPage(Page rightPage, Slice seperatorKey)
        {
            if (_parentPage.SizeLeft < SizeOf.BranchEntry(seperatorKey) + Constants.NodeOffsetSize)
            {
                new PageSplitter(_tx, _cmp, seperatorKey, -1, rightPage.PageNumber, NodeFlags.PageRef, 0, _cursor, _txInfo).Execute();
            }
            else
            {
                _parentPage.NodePositionFor(seperatorKey, _cmp); // select the appropriate place for this
				_parentPage.AddPageRefNode(_parentPage.LastSearchPosition, seperatorKey, rightPage.PageNumber);
            }
        }


        /// <summary>
        /// For leaf pages, check the split point based on what
        ///	fits where, since otherwise adding the node can fail.
        ///	
        ///	This check is only needed when the data items are
        ///	relatively large, such that being off by one will
        ///	make the difference between success or failure.
        ///	
        ///	It's also relevant if a page happens to be laid out
        ///	such that one half of its nodes are all "small" and
        ///	the other half of its nodes are "large." If the new
        ///	item is also "large" and falls on the half with
        ///	"large" nodes, it also may not fit.
        /// </summary>
        private int AdjustSplitPosition(Slice key, int len, Page page, int currentIndex, int splitIndex,
                                                      ref bool newPosition)
        {
            var nodeSize = SizeOf.NodeEntry(_tx.PagerInfo.PageMaxSpace, key, len) + Constants.NodeOffsetSize;
			if (page.NumberOfEntries >= 20 && nodeSize <= _tx.PagerInfo.PageMaxSpace / 16)
            {
                return splitIndex;
            }

            int pageSize = nodeSize;
            if (currentIndex <= splitIndex)
            {
                newPosition = false;
                for (int i = 0; i < splitIndex; i++)
                {
                    var node = page.GetNode(i);
                    pageSize += node->GetNodeSize();
                    pageSize += pageSize & 1;
					if (pageSize > _tx.PagerInfo.PageMaxSpace)
                    {
                        if (i <= currentIndex)
                        {
                            if (i < currentIndex)
                                newPosition = true;
                            return currentIndex;
                        }
                        return (ushort)i;
                    }
                }
            }
            else
            {
                for (int i = page.NumberOfEntries - 1; i >= splitIndex; i--)
                {
                    var node = page.GetNode(i);
                    pageSize += node->GetNodeSize();
                    pageSize += pageSize & 1;
					if (pageSize > _tx.PagerInfo.PageMaxSpace)
                    {
                        if (i >= currentIndex)
                        {
                            newPosition = false;
                            return currentIndex;
                        }
                        return (ushort)(i + 1);
                    }
                }
            }
            return splitIndex;
        }
    }
}