﻿// -----------------------------------------------------------------------
//  <copyright file="LogFile.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Voron.Trees;
using Voron.Util;

namespace Voron.Impl.Log
{
	public class CommitPoint
	{
		public long LogNumber;
		public long TxId;
		public long TxLastPageNumber;
		public long LastWrittenLogPage;
	}

	public unsafe class LogFile : IDisposable
	{
		private const int PagesTakenByHeader = 1;

		private readonly IVirtualPager _pager;
		private ImmutableDictionary<long, long> _pageTranslationTable = ImmutableDictionary<long, long>.Empty;
		private readonly Dictionary<long, long> _transactionPageTranslationTable = new Dictionary<long, long>();
		private long _writePage = 0;
		private long _lastSyncedPage = -1;
		private int _allocatedPagesInTransaction = 0;
		private int _overflowPagesInTransaction = 0;
		private TransactionHeader* _currentTxHeader = null;
		private bool _disposed;

		public LogFile(IVirtualPager pager, long logNumber)
		{
			Number = logNumber;
			_pager = pager;
			_writePage = 0;
		}

		public LogFile(IVirtualPager pager, long logNumber, long lastSyncedPage)
			: this(pager, logNumber)
		{
			_lastSyncedPage = lastSyncedPage;
			_writePage = lastSyncedPage + 1;
		}

		~LogFile()
		{
			if (_disposed == false)
			{
				Dispose();

				Trace.WriteLine(
					"Disposing a log file from finalizer! It should be diposed by using LogFile.Release() instead!. Log file number: " +
					Number + ". Number of references: " + _refs);
			}
		}

		internal long WritePagePosition
		{
			get { return _writePage; }
		}

		public long Number { get; private set; }

		public CommitPoint LastCommit { get; private set; }

		public IEnumerable<long> GetModifiedPages(long? lastLogPageSyncedWithDataFile)
		{
			if(lastLogPageSyncedWithDataFile == null)
				return _pageTranslationTable.Keys;

			return _pageTranslationTable.Where(x => x.Value > lastLogPageSyncedWithDataFile).Select(x => x.Key);
		}

		public bool LastTransactionCommitted
		{
			get
			{
				if (_currentTxHeader != null)
				{
					Debug.Assert(_currentTxHeader->TxMarker.HasFlag(TransactionMarker.Commit) == false);
					return false;
				}
				return true;
			}
		}

		public void TransactionBegin(Transaction tx)
		{
			if (LastTransactionCommitted == false)
			{
				// last transaction did not commit, we need to move back the write page position
				_writePage = _lastSyncedPage + 1;
			}

			_currentTxHeader = GetTransactionHeader();

			_currentTxHeader->TxId = tx.Id;
			_currentTxHeader->NextPageNumber = tx.NextPageNumber;
			_currentTxHeader->LastPageNumber = -1;
			_currentTxHeader->PageCount = -1;
			_currentTxHeader->Crc = 0;
			_currentTxHeader->TxMarker = TransactionMarker.Start;

			_allocatedPagesInTransaction = 0;
			_overflowPagesInTransaction = 0;

			_transactionPageTranslationTable.Clear();
		}

		public void TransactionSplit(Transaction tx)
		{
			if (_currentTxHeader != null)
			{
				_currentTxHeader->TxMarker |= TransactionMarker.Split;
				_currentTxHeader->PageCount = _allocatedPagesInTransaction;
			}
			else
			{
				_currentTxHeader = GetTransactionHeader();
				_currentTxHeader->TxId = tx.Id;
				_currentTxHeader->NextPageNumber = tx.NextPageNumber;
				_currentTxHeader->TxMarker = TransactionMarker.Split;
				_currentTxHeader->PageCount = -1;
				_currentTxHeader->Crc = 0;
			}	
		}

		public void TransactionCommit(Transaction tx)
		{
			_pageTranslationTable = _pageTranslationTable.AddRange(_transactionPageTranslationTable);

			LastCommit = new CommitPoint()
				{
					LogNumber = Number,
					TxId = tx.Id,
					TxLastPageNumber = tx.NextPageNumber - 1,
					LastWrittenLogPage = _writePage - 1
				};

			_currentTxHeader->LastPageNumber = tx.NextPageNumber - 1;
			_currentTxHeader->TxMarker |= TransactionMarker.Commit;
			_currentTxHeader->PageCount = _allocatedPagesInTransaction;
			_currentTxHeader->OverflowPageCount = _overflowPagesInTransaction;
			tx.Environment.Root.State.CopyTo(&_currentTxHeader->Root);

			var crcOffset = (int) (_currentTxHeader->PageNumberInLogFile + PagesTakenByHeader)*_pager.PageSize;
			var crcCount = (_allocatedPagesInTransaction + _overflowPagesInTransaction)*_pager.PageSize;

			_currentTxHeader->Crc = Crc.Value(_pager.PagerState.Base, crcOffset, crcCount);

			//TODO free space copy

			_currentTxHeader = null;

			Sync();
		}


		private TransactionHeader* GetTransactionHeader()
		{
			var result = (TransactionHeader*) Allocate(-1, PagesTakenByHeader).Base;
			result->HeaderMarker = Constants.TransactionHeaderMarker;
			result->PageNumberInLogFile = _writePage - PagesTakenByHeader;

			return result;
		}

		public long AvailablePages
		{
			get { return _pager.NumberOfAllocatedPages - _writePage; }
		}

		internal IVirtualPager Pager
		{
			get { return _pager; }
		}

		private void Sync()
		{
			var start = _lastSyncedPage + 1;
			var count = _writePage - start;

			_pager.Flush(start, count);
			_pager.Sync();

			_lastSyncedPage += count;
		}

		public Page ReadPage(Transaction tx, long pageNumber)
		{
			long logPageNumber;

			if (tx != null &&
				_currentTxHeader != null && _currentTxHeader->TxId == tx.Id // we are in the log file where we are currently writing in
				&& _transactionPageTranslationTable.TryGetValue(pageNumber, out logPageNumber))
				return _pager.Read(logPageNumber);

			if (_pageTranslationTable.TryGetValue(pageNumber, out logPageNumber))
				return _pager.Read(logPageNumber);
			
			return null;
		}

		public Page Allocate(long startPage, int numberOfPages)
		{
			Debug.Assert(_writePage + numberOfPages <= _pager.NumberOfAllocatedPages);

			var result = _pager.GetWritable(_writePage);

			if (startPage != -1) // internal use - transaction header allocation, so we don't want to count it as allocated by transaction
			{
				// we allocate more than one page only if the page is an overflow
				// so here we don't want to create mapping for them too
				_transactionPageTranslationTable[startPage] = _writePage;

				_allocatedPagesInTransaction++;

				if (numberOfPages > 1)
				{
					_overflowPagesInTransaction += (numberOfPages - 1);
				}
			}

			_writePage += numberOfPages;

			return result;
		}

		public TransactionHeader* RecoverAndValidate(long startReadingPage, TransactionHeader* previous)
		{
			TransactionHeader* lastReadHeader = previous;

			var readPosition = startReadingPage;

			while (readPosition < _pager.NumberOfAllocatedPages)
			{
				var current = (TransactionHeader*)_pager.Read(readPosition).Base;

				if(current->HeaderMarker != Constants.TransactionHeaderMarker)
					break;

				ValidateHeader(current, lastReadHeader);

				if (current->TxMarker.HasFlag(TransactionMarker.Commit) == false && current->TxMarker.HasFlag(TransactionMarker.Split) == false)
				{
					readPosition += current->PageCount + current->OverflowPageCount;
					continue;
				}

				lastReadHeader = current;

				readPosition++;

				var transactionPageTranslation = new Dictionary<long, long>();

				uint crc = 0;

				for (var i = 0; i < current->PageCount; i++)
				{
					var page = _pager.Read(readPosition);

					transactionPageTranslation[page.PageNumber] = readPosition;

					if (page.IsOverflow)
					{
						var numOfPages = Page.GetNumberOfOverflowPages(_pager.PageSize, page.OverflowSize);
						readPosition += numOfPages;
						crc = Crc.Extend(crc, page.Base, 0, numOfPages*_pager.PageSize);
					}
					else
					{
						readPosition++;
						crc = Crc.Extend(crc, page.Base, 0, _pager.PageSize);
					}

					_lastSyncedPage = readPosition - 1;
					_writePage = _lastSyncedPage + 1;
				}

				if (crc != current->Crc)
				{
					throw new InvalidDataException("Checksum mismatch"); //TODO this is temporary, ini the future this condition will just mean that transaction was not committed
				}

				_pageTranslationTable = _pageTranslationTable.AddRange(transactionPageTranslation);
			}

			return lastReadHeader;
		}

		private void ValidateHeader(TransactionHeader* current, TransactionHeader* previous)
		{
			if (current->TxId < 0)
				throw new InvalidDataException("Transaction id cannot be less than 0 (Tx: " + current->TxId);
			if (current->TxMarker.HasFlag(TransactionMarker.Start) == false)
				throw new InvalidDataException("Transaction must have Start marker");
			if (current->LastPageNumber < 0)
				throw new InvalidDataException("Last page number after committed transaction must be greater than 0");
			if(current->PageCount > 0 && current->Crc == 0)
				throw new InvalidDataException("Transaction checksum can't be equal to 0");

			if (previous == null) 
				return;

			if (previous->TxMarker.HasFlag(TransactionMarker.Split))
			{
				if(current->TxMarker.HasFlag(TransactionMarker.Split) == false)
					throw new InvalidDataException("Previous transaction have a split marker, so the current one should have it too");

				if (current->TxId == previous->TxId)
					throw new InvalidDataException("Split transaction should have the same id in the log. Expected id: " +
					                               previous->TxId + ", got: " + current->TxId);
			}
			else
			{
				if (current->TxId != 1 && // 1 is a first storage transaction which does not increment transaction counter after commit
					current->TxId - previous->TxId != 1)
					throw new InvalidDataException("Unexpected transaction id. Expected: " + (previous->TxId + 1) + ", got:" +
					                               current->TxId);
			}
			
		}

		public void DeleteOnClose()
		{
			_pager.DeleteOnClose = true;
		}

		private int _refs;

		public void Release()
		{
			if (Interlocked.Decrement(ref _refs) != 0)
				return;

			Dispose();
		}

		public void AddRef()
		{
			Interlocked.Increment(ref _refs);
		}

		public void Dispose()
		{
			if(_disposed)
				throw new ObjectDisposedException("Log file is already disposed");

			_pager.Dispose();

			_disposed = true;
		}

		public LogSnapshot GetSnapshot()
		{
			return new LogSnapshot
				{
					File = this,
					PageTranslations = _pageTranslationTable
				};
		}
	}
}