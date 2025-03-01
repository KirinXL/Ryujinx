using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Memory.WindowsShared
{
    /// <summary>
    /// Windows memory placeholder manager.
    /// </summary>
    [SupportedOSPlatform("windows")]
    class PlaceholderManager
    {
        private const int InitialOverlapsSize = 10;

        [ThreadStatic]
        private static int _threadLocalPartialUnmapsCount;

        private readonly MappingTree<ulong> _mappings;
        private readonly MappingTree<MemoryPermission> _protections;
        private readonly ReaderWriterLock _partialUnmapLock;
        private int _partialUnmapsCount;

        /// <summary>
        /// Creates a new instance of the Windows memory placeholder manager.
        /// </summary>
        public PlaceholderManager()
        {
            _mappings = new MappingTree<ulong>();
            _protections = new MappingTree<MemoryPermission>();
            _partialUnmapLock = new ReaderWriterLock();
        }

        /// <summary>
        /// Reserves a range of the address space to be later mapped as shared memory views.
        /// </summary>
        /// <param name="address">Start address of the region to reserve</param>
        /// <param name="size">Size in bytes of the region to reserve</param>
        public void ReserveRange(ulong address, ulong size)
        {
            lock (_mappings)
            {
                _mappings.Add(new RangeNode<ulong>(address, address + size, ulong.MaxValue));
            }

            lock (_protections)
            {
                _protections.Add(new RangeNode<MemoryPermission>(address, size, MemoryPermission.None));
            }
        }

        /// <summary>
        /// Unreserves a range of memory that has been previously reserved with <see cref="ReserveRange"/>.
        /// </summary>
        /// <param name="address">Start address of the region to unreserve</param>
        /// <param name="size">Size in bytes of the region to unreserve</param>
        /// <exception cref="WindowsApiException">Thrown when the Windows API returns an error unreserving the memory</exception>
        public void UnreserveRange(ulong address, ulong size)
        {
            ulong endAddress = address + size;

            var overlaps = new RangeNode<ulong>[InitialOverlapsSize];
            int count;

            lock (_mappings)
            {
                count = _mappings.GetNodes(address, endAddress, ref overlaps);

                for (int index = 0; index < count; index++)
                {
                    var overlap = overlaps[index];

                    if (IsMapped(overlap.Value))
                    {
                        if (!WindowsApi.UnmapViewOfFile2(WindowsApi.CurrentProcessHandle, (IntPtr)overlap.Start, 2))
                        {
                            throw new WindowsApiException("UnmapViewOfFile2");
                        }
                    }

                    _mappings.Remove(overlap);
                }
            }

            RemoveProtection(address, size);
        }

        /// <summary>
        /// Maps a shared memory view on a previously reserved memory region.
        /// </summary>
        /// <param name="sharedMemory">Shared memory that will be the backing storage for the view</param>
        /// <param name="srcOffset">Offset in the shared memory to map</param>
        /// <param name="location">Address to map the view into</param>
        /// <param name="size">Size of the view in bytes</param>
        /// <param name="owner">Memory block that owns the mapping</param>
        public void MapView(IntPtr sharedMemory, ulong srcOffset, IntPtr location, IntPtr size, MemoryBlock owner)
        {
            _partialUnmapLock.AcquireReaderLock(Timeout.Infinite);

            try
            {
                UnmapViewInternal(sharedMemory, location, size, owner, updateProtection: false);
                MapViewInternal(sharedMemory, srcOffset, location, size);
            }
            finally
            {
                _partialUnmapLock.ReleaseReaderLock();
            }
        }

        /// <summary>
        /// Maps a shared memory view on a previously reserved memory region.
        /// </summary>
        /// <param name="sharedMemory">Shared memory that will be the backing storage for the view</param>
        /// <param name="srcOffset">Offset in the shared memory to map</param>
        /// <param name="location">Address to map the view into</param>
        /// <param name="size">Size of the view in bytes</param>
        /// <exception cref="WindowsApiException">Thrown when the Windows API returns an error mapping the memory</exception>
        private void MapViewInternal(IntPtr sharedMemory, ulong srcOffset, IntPtr location, IntPtr size)
        {
            SplitForMap((ulong)location, (ulong)size, srcOffset);

            var ptr = WindowsApi.MapViewOfFile3(
                sharedMemory,
                WindowsApi.CurrentProcessHandle,
                location,
                srcOffset,
                size,
                0x4000,
                MemoryProtection.ReadWrite,
                IntPtr.Zero,
                0);

            if (ptr == IntPtr.Zero)
            {
                throw new WindowsApiException("MapViewOfFile3");
            }

            UpdateProtection((ulong)location, (ulong)size, MemoryPermission.ReadAndWrite);
        }

        /// <summary>
        /// Splits a larger placeholder, slicing at the start and end address, for a new memory mapping.
        /// </summary>
        /// <param name="address">Address to split</param>
        /// <param name="size">Size of the new region</param>
        /// <param name="backingOffset">Offset in the shared memory that will be mapped</param>
        private void SplitForMap(ulong address, ulong size, ulong backingOffset)
        {
            ulong endAddress = address + size;

            var overlaps = new RangeNode<ulong>[InitialOverlapsSize];

            lock (_mappings)
            {
                int count = _mappings.GetNodes(address, endAddress, ref overlaps);

                Debug.Assert(count == 1);
                Debug.Assert(!IsMapped(overlaps[0].Value));

                var overlap = overlaps[0];

                ulong overlapStart = overlap.Start;
                ulong overlapEnd = overlap.End;
                ulong overlapValue = overlap.Value;

                _mappings.Remove(overlap);

                bool overlapStartsBefore = overlapStart < address;
                bool overlapEndsAfter = overlapEnd > endAddress;

                if (overlapStartsBefore && overlapEndsAfter)
                {
                    CheckFreeResult(WindowsApi.VirtualFree(
                        (IntPtr)address,
                        (IntPtr)size,
                        AllocationType.Release | AllocationType.PreservePlaceholder));

                    _mappings.Add(new RangeNode<ulong>(overlapStart, address, overlapValue));
                    _mappings.Add(new RangeNode<ulong>(endAddress, overlapEnd, AddBackingOffset(overlapValue, endAddress - overlapStart)));
                }
                else if (overlapStartsBefore)
                {
                    ulong overlappedSize = overlapEnd - address;

                    CheckFreeResult(WindowsApi.VirtualFree(
                        (IntPtr)address,
                        (IntPtr)overlappedSize,
                        AllocationType.Release | AllocationType.PreservePlaceholder));

                    _mappings.Add(new RangeNode<ulong>(overlapStart, address, overlapValue));
                }
                else if (overlapEndsAfter)
                {
                    ulong overlappedSize = endAddress - overlapStart;

                    CheckFreeResult(WindowsApi.VirtualFree(
                        (IntPtr)overlapStart,
                        (IntPtr)overlappedSize,
                        AllocationType.Release | AllocationType.PreservePlaceholder));

                    _mappings.Add(new RangeNode<ulong>(endAddress, overlapEnd, AddBackingOffset(overlapValue, overlappedSize)));
                }

                _mappings.Add(new RangeNode<ulong>(address, endAddress, backingOffset));
            }
        }

        /// <summary>
        /// Unmaps a view that has been previously mapped with <see cref="MapView"/>.
        /// </summary>
        /// <remarks>
        /// For "partial unmaps" (when not the entire mapped range is being unmapped), it might be
        /// necessary to unmap the whole range and then remap the sub-ranges that should remain mapped.
        /// </remarks>
        /// <param name="sharedMemory">Shared memory that the view being unmapped belongs to</param>
        /// <param name="location">Address to unmap</param>
        /// <param name="size">Size of the region to unmap in bytes</param>
        /// <param name="owner">Memory block that owns the mapping</param>
        public void UnmapView(IntPtr sharedMemory, IntPtr location, IntPtr size, MemoryBlock owner)
        {
            _partialUnmapLock.AcquireReaderLock(Timeout.Infinite);

            try
            {
                UnmapViewInternal(sharedMemory, location, size, owner, updateProtection: true);
            }
            finally
            {
                _partialUnmapLock.ReleaseReaderLock();
            }
        }

        /// <summary>
        /// Unmaps a view that has been previously mapped with <see cref="MapView"/>.
        /// </summary>
        /// <remarks>
        /// For "partial unmaps" (when not the entire mapped range is being unmapped), it might be
        /// necessary to unmap the whole range and then remap the sub-ranges that should remain mapped.
        /// </remarks>
        /// <param name="sharedMemory">Shared memory that the view being unmapped belongs to</param>
        /// <param name="location">Address to unmap</param>
        /// <param name="size">Size of the region to unmap in bytes</param>
        /// <param name="owner">Memory block that owns the mapping</param>
        /// <param name="updateProtection">Indicates if the memory protections should be updated after the unmap</param>
        /// <exception cref="WindowsApiException">Thrown when the Windows API returns an error unmapping or remapping the memory</exception>
        private void UnmapViewInternal(IntPtr sharedMemory, IntPtr location, IntPtr size, MemoryBlock owner, bool updateProtection)
        {
            ulong startAddress = (ulong)location;
            ulong unmapSize = (ulong)size;
            ulong endAddress = startAddress + unmapSize;

            var overlaps = new RangeNode<ulong>[InitialOverlapsSize];
            int count;

            lock (_mappings)
            {
                count = _mappings.GetNodes(startAddress, endAddress, ref overlaps);
            }

            for (int index = 0; index < count; index++)
            {
                var overlap = overlaps[index];

                if (IsMapped(overlap.Value))
                {
                    lock (_mappings)
                    {
                        _mappings.Remove(overlap);
                        _mappings.Add(new RangeNode<ulong>(overlap.Start, overlap.End, ulong.MaxValue));
                    }

                    bool overlapStartsBefore = overlap.Start < startAddress;
                    bool overlapEndsAfter = overlap.End > endAddress;

                    if (overlapStartsBefore || overlapEndsAfter)
                    {
                        // If the overlap extends beyond the region we are unmapping,
                        // then we need to re-map the regions that are supposed to remain mapped.
                        // This is necessary because Windows does not support partial view unmaps.
                        // That is, you can only fully unmap a view that was previously mapped, you can't just unmap a chunck of it.

                        LockCookie lockCookie = _partialUnmapLock.UpgradeToWriterLock(Timeout.Infinite);

                        _partialUnmapsCount++;

                        if (!WindowsApi.UnmapViewOfFile2(WindowsApi.CurrentProcessHandle, (IntPtr)overlap.Start, 2))
                        {
                            throw new WindowsApiException("UnmapViewOfFile2");
                        }

                        if (overlapStartsBefore)
                        {
                            ulong remapSize = startAddress - overlap.Start;

                            MapViewInternal(sharedMemory, overlap.Value, (IntPtr)overlap.Start, (IntPtr)remapSize);
                            RestoreRangeProtection(overlap.Start, remapSize);
                        }

                        if (overlapEndsAfter)
                        {
                            ulong overlappedSize = endAddress - overlap.Start;
                            ulong remapBackingOffset = overlap.Value + overlappedSize;
                            ulong remapAddress = overlap.Start + overlappedSize;
                            ulong remapSize = overlap.End - endAddress;

                            MapViewInternal(sharedMemory, remapBackingOffset, (IntPtr)remapAddress, (IntPtr)remapSize);
                            RestoreRangeProtection(remapAddress, remapSize);
                        }

                        _partialUnmapLock.DowngradeFromWriterLock(ref lockCookie);
                    }
                    else if (!WindowsApi.UnmapViewOfFile2(WindowsApi.CurrentProcessHandle, (IntPtr)overlap.Start, 2))
                    {
                        throw new WindowsApiException("UnmapViewOfFile2");
                    }
                }
            }

            CoalesceForUnmap(startAddress, unmapSize, owner);

            if (updateProtection)
            {
                UpdateProtection(startAddress, unmapSize, MemoryPermission.None);
            }
        }

        /// <summary>
        /// Coalesces adjacent placeholders after unmap.
        /// </summary>
        /// <param name="address">Address of the region that was unmapped</param>
        /// <param name="size">Size of the region that was unmapped in bytes</param>
        /// <param name="owner">Memory block that owns the mapping</param>
        private void CoalesceForUnmap(ulong address, ulong size, MemoryBlock owner)
        {
            ulong endAddress = address + size;
            ulong blockAddress = (ulong)owner.Pointer;
            ulong blockEnd = blockAddress + owner.Size;
            var overlaps = new RangeNode<ulong>[InitialOverlapsSize];
            int unmappedCount = 0;

            lock (_mappings)
            {
                int count = _mappings.GetNodes(address, endAddress, ref overlaps);

                if (count == 0)
                {
                    // Nothing to coalesce if we no overlaps.
                    return;
                }

                RangeNode<ulong> predecessor = overlaps[0].Predecessor;
                RangeNode<ulong> successor = overlaps[count - 1].Successor;

                for (int index = 0; index < count; index++)
                {
                    var overlap = overlaps[index];

                    if (!IsMapped(overlap.Value))
                    {
                        address = Math.Min(address, overlap.Start);
                        endAddress = Math.Max(endAddress, overlap.End);

                        _mappings.Remove(overlap);
                        unmappedCount++;
                    }
                }

                if (predecessor != null && !IsMapped(predecessor.Value) && predecessor.Start >= blockAddress)
                {
                    address = Math.Min(address, predecessor.Start);

                    _mappings.Remove(predecessor);
                    unmappedCount++;
                }

                if (successor != null && !IsMapped(successor.Value) && successor.End <= blockEnd)
                {
                    endAddress = Math.Max(endAddress, successor.End);

                    _mappings.Remove(successor);
                    unmappedCount++;
                }

                _mappings.Add(new RangeNode<ulong>(address, endAddress, ulong.MaxValue));
            }

            if (unmappedCount > 1)
            {
                size = endAddress - address;

                CheckFreeResult(WindowsApi.VirtualFree(
                    (IntPtr)address,
                    (IntPtr)size,
                    AllocationType.Release | AllocationType.CoalescePlaceholders));
            }
        }

        /// <summary>
        /// Reprotects a region of memory that has been mapped.
        /// </summary>
        /// <param name="address">Address of the region to reprotect</param>
        /// <param name="size">Size of the region to reprotect in bytes</param>
        /// <param name="permission">New permissions</param>
        /// <returns>True if the reprotection was successful, false otherwise</returns>
        public bool ReprotectView(IntPtr address, IntPtr size, MemoryPermission permission)
        {
            _partialUnmapLock.AcquireReaderLock(Timeout.Infinite);

            try
            {
                return ReprotectViewInternal(address, size, permission, false);
            }
            finally
            {
                _partialUnmapLock.ReleaseReaderLock();
            }
        }

        /// <summary>
        /// Reprotects a region of memory that has been mapped.
        /// </summary>
        /// <param name="address">Address of the region to reprotect</param>
        /// <param name="size">Size of the region to reprotect in bytes</param>
        /// <param name="permission">New permissions</param>
        /// <param name="throwOnError">Throw an exception instead of returning an error if the operation fails</param>
        /// <returns>True if the reprotection was successful or if <paramref name="throwOnError"/> is true, false otherwise</returns>
        /// <exception cref="WindowsApiException">If <paramref name="throwOnError"/> is true, it is thrown when the Windows API returns an error reprotecting the memory</exception>
        private bool ReprotectViewInternal(IntPtr address, IntPtr size, MemoryPermission permission, bool throwOnError)
        {
            ulong reprotectAddress = (ulong)address;
            ulong reprotectSize = (ulong)size;
            ulong endAddress = reprotectAddress + reprotectSize;

            var overlaps = new RangeNode<ulong>[InitialOverlapsSize];
            int count;

            lock (_mappings)
            {
                count = _mappings.GetNodes(reprotectAddress, endAddress, ref overlaps);
            }

            bool success = true;

            for (int index = 0; index < count; index++)
            {
                var overlap = overlaps[index];

                ulong mappedAddress = overlap.Start;
                ulong mappedSize = overlap.End - overlap.Start;

                if (mappedAddress < reprotectAddress)
                {
                    ulong delta = reprotectAddress - mappedAddress;
                    mappedAddress = reprotectAddress;
                    mappedSize -= delta;
                }

                ulong mappedEndAddress = mappedAddress + mappedSize;

                if (mappedEndAddress > endAddress)
                {
                    ulong delta = mappedEndAddress - endAddress;
                    mappedSize -= delta;
                }

                if (!WindowsApi.VirtualProtect((IntPtr)mappedAddress, (IntPtr)mappedSize, WindowsApi.GetProtection(permission), out _))
                {
                    if (throwOnError)
                    {
                        throw new WindowsApiException("VirtualProtect");
                    }

                    success = false;
                }
            }

            UpdateProtection(reprotectAddress, reprotectSize, permission);

            return success;
        }

        /// <summary>
        /// Checks the result of a VirtualFree operation, throwing if needed.
        /// </summary>
        /// <param name="success">Operation result</param>
        /// <exception cref="WindowsApiException">Thrown if <paramref name="success"/> is false</exception>
        private static void CheckFreeResult(bool success)
        {
            if (!success)
            {
                throw new WindowsApiException("VirtualFree");
            }
        }

        /// <summary>
        /// Adds an offset to a backing offset. This will do nothing if the backing offset is the special "unmapped" value.
        /// </summary>
        /// <param name="backingOffset">Backing offset</param>
        /// <param name="offset">Offset to be added</param>
        /// <returns>Added offset or just <paramref name="backingOffset"/> if the region is unmapped</returns>
        private static ulong AddBackingOffset(ulong backingOffset, ulong offset)
        {
            if (backingOffset == ulong.MaxValue)
            {
                return backingOffset;
            }

            return backingOffset + offset;
        }

        /// <summary>
        /// Checks if a region is unmapped.
        /// </summary>
        /// <param name="backingOffset">Backing offset to check</param>
        /// <returns>True if the backing offset is the special "unmapped" value, false otherwise</returns>
        private static bool IsMapped(ulong backingOffset)
        {
            return backingOffset != ulong.MaxValue;
        }

        /// <summary>
        /// Adds a protection to the list of protections.
        /// </summary>
        /// <param name="address">Address of the protected region</param>
        /// <param name="size">Size of the protected region in bytes</param>
        /// <param name="permission">Memory permissions of the region</param>
        private void UpdateProtection(ulong address, ulong size, MemoryPermission permission)
        {
            ulong endAddress = address + size;
            var overlaps = new RangeNode<MemoryPermission>[InitialOverlapsSize];
            int count;

            lock (_protections)
            {
                count = _protections.GetNodes(address, endAddress, ref overlaps);

                if (count == 1 &&
                    overlaps[0].Start <= address &&
                    overlaps[0].End >= endAddress &&
                    overlaps[0].Value == permission)
                {
                    return;
                }

                ulong startAddress = address;

                for (int index = 0; index < count; index++)
                {
                    var protection = overlaps[index];

                    ulong protAddress = protection.Start;
                    ulong protEndAddress = protection.End;
                    MemoryPermission protPermission = protection.Value;

                    _protections.Remove(protection);

                    if (protection.Value == permission)
                    {
                        if (startAddress > protAddress)
                        {
                            startAddress = protAddress;
                        }

                        if (endAddress < protEndAddress)
                        {
                            endAddress = protEndAddress;
                        }
                    }
                    else
                    {
                        if (startAddress > protAddress)
                        {
                            _protections.Add(new RangeNode<MemoryPermission>(protAddress, startAddress, protPermission));
                        }

                        if (endAddress < protEndAddress)
                        {
                            _protections.Add(new RangeNode<MemoryPermission>(endAddress, protEndAddress, protPermission));
                        }
                    }
                }

                _protections.Add(new RangeNode<MemoryPermission>(startAddress, endAddress, permission));
            }
        }

        /// <summary>
        /// Removes protection from the list of protections.
        /// </summary>
        /// <param name="address">Address of the protected region</param>
        /// <param name="size">Size of the protected region in bytes</param>
        private void RemoveProtection(ulong address, ulong size)
        {
            ulong endAddress = address + size;
            var overlaps = new RangeNode<MemoryPermission>[InitialOverlapsSize];
            int count;

            lock (_protections)
            {
                count = _protections.GetNodes(address, endAddress, ref overlaps);

                for (int index = 0; index < count; index++)
                {
                    var protection = overlaps[index];

                    ulong protAddress = protection.Start;
                    ulong protEndAddress = protection.End;
                    MemoryPermission protPermission = protection.Value;

                    _protections.Remove(protection);

                    if (address > protAddress)
                    {
                        _protections.Add(new RangeNode<MemoryPermission>(protAddress, address, protPermission));
                    }

                    if (endAddress < protEndAddress)
                    {
                        _protections.Add(new RangeNode<MemoryPermission>(endAddress, protEndAddress, protPermission));
                    }
                }
            }
        }

        /// <summary>
        /// Restores the protection of a given memory region that was remapped, using the protections list.
        /// </summary>
        /// <param name="address">Address of the remapped region</param>
        /// <param name="size">Size of the remapped region in bytes</param>
        private void RestoreRangeProtection(ulong address, ulong size)
        {
            ulong endAddress = address + size;
            var overlaps = new RangeNode<MemoryPermission>[InitialOverlapsSize];
            int count;

            lock (_protections)
            {
                count = _protections.GetNodes(address, endAddress, ref overlaps);
            }

            ulong startAddress = address;

            for (int index = 0; index < count; index++)
            {
                var protection = overlaps[index];

                // If protection is R/W we don't need to reprotect as views are initially mapped as R/W.
                if (protection.Value == MemoryPermission.ReadAndWrite)
                {
                    continue;
                }

                ulong protAddress = protection.Start;
                ulong protEndAddress = protection.End;

                if (protAddress < address)
                {
                    protAddress = address;
                }

                if (protEndAddress > endAddress)
                {
                    protEndAddress = endAddress;
                }

                ReprotectViewInternal((IntPtr)protAddress, (IntPtr)(protEndAddress - protAddress), protection.Value, true);
            }
        }

        /// <summary>
        /// Checks if an access violation handler should retry execution due to a fault caused by partial unmap.
        /// </summary>
        /// <remarks>
        /// Due to Windows limitations, <see cref="UnmapView"/> might need to unmap more memory than requested.
        /// The additional memory that was unmapped is later remapped, however this leaves a time gap where the
        /// memory might be accessed but is unmapped. Users of the API must compensate for that by catching the
        /// access violation and retrying if it happened between the unmap and remap operation.
        /// This method can be used to decide if retrying in such cases is necessary or not.
        /// </remarks>
        /// <returns>True if execution should be retried, false otherwise</returns>
        public bool RetryFromAccessViolation()
        {
            _partialUnmapLock.AcquireReaderLock(Timeout.Infinite);

            bool retry = _threadLocalPartialUnmapsCount != _partialUnmapsCount;
            if (retry)
            {
                _threadLocalPartialUnmapsCount = _partialUnmapsCount;
            }

            _partialUnmapLock.ReleaseReaderLock();

            return retry;
        }
    }
}