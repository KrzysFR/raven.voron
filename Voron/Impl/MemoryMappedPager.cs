﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Linq;
using Voron.Trees;

namespace Voron.Impl
{
	public unsafe class MemoryMapPager : AbstractPager
	{
		private readonly FlushMode _flushMode;
		private readonly FileStream _fileStream;

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		extern static bool FlushViewOfFile(byte* lpBaseAddress, IntPtr dwNumberOfBytesToFlush);

		public MemoryMapPager(string file, FlushMode flushMode = FlushMode.Full)
		{
			_flushMode = flushMode;
			var fileInfo = new FileInfo(file);
			var hasData = fileInfo.Exists == false || fileInfo.Length == 0;
			_fileStream = fileInfo.Open(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
			if (hasData)
			{
				NumberOfAllocatedPages = 0;
			}
			else
			{
				NumberOfAllocatedPages = fileInfo.Length / PageSize;
				PagerState.Release();
				PagerState = CreateNewPagerState();
			}
		}

		public override void AllocateMorePages(Transaction tx, long newLength)
		{
			if (newLength < _fileStream.Length)
				throw new ArgumentException("Cannot set the legnth to less than the current length");

			if (newLength == _fileStream.Length)
				return;

			// need to allocate memory again
			_fileStream.SetLength(newLength);
			PagerState.Release(); // when the last transaction using this is over, will dispose it
			PagerState newPager = CreateNewPagerState();

			if (tx != null) // we only pass null during startup, and we don't need it there
			{
				newPager.AddRef(); // one for the current transaction
				tx.AddPagerState(newPager);
			}

			PagerState = newPager;
			NumberOfAllocatedPages = newPager.Accessor.Capacity / PageSize;
		}

		private PagerState CreateNewPagerState()
		{
			var mmf = MemoryMappedFile.CreateFromFile(_fileStream, Guid.NewGuid().ToString(), _fileStream.Length,
													  MemoryMappedFileAccess.ReadWrite, null, HandleInheritability.None, true);
			var accessor = mmf.CreateViewAccessor();
			byte* p = null;
			accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref p);

			var newPager = new PagerState
			{
				Accessor = accessor,
				File = mmf,
				Base = p
			};
			newPager.AddRef(); // one for the pager
			return newPager;
		}

		public override byte* AcquirePagePointer(long pageNumber)
		{
			return PagerState.Base + (pageNumber * PageSize);
		}

		public override void Sync()
		{
			if (_flushMode == FlushMode.Full)
				_fileStream.Flush(true);
		}

		public override int Write(Page page, long writeToPage)
		{
			var position = writeToPage * PageSize;

			var toWrite = page.IsOverflow ? page.OverflowSize : PageSize;

			NativeMethods.memcpy(PagerState.Base + position, page.Base, toWrite);

			return toWrite;
		}

		public override int Write(Page page)
		{
			return Write(page, page.PageNumber);
		}

		public override void Flush(long startPage, long count)
		{
			long numberOfBytesToFlush = count * PageSize;
			long start = startPage * PageSize;
			FlushViewOfFile(PagerState.Base + start, new IntPtr(numberOfBytesToFlush));
		}

		public override void Dispose()
		{
			base.Dispose();
			if (PagerState != null)
			{
				PagerState.Release();
				PagerState = null;
			}
			_fileStream.Dispose();
		}
	}
}