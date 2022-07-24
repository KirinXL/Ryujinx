using System;
using System.Runtime.Versioning;

namespace Ryujinx.Memory.WindowsShared
{
    /// <summary>
    /// Windows 4KB memory placeholder manager.
    /// </summary>
    [SupportedOSPlatform("windows")]
    class PlaceholderManager4KB
    {
        private const int InitialOverlapsSize = 10;

        private const int PageSize = MemoryManagementWindows.PageSize;

        private enum MappingState : byte
        {
            Unmapped,
            Mapped
        }

        private readonly MappingTree<MappingState> _mappings;

        /// <summary>
        /// Creates a new instance of the Windows 4KB memory placeholder manager.
        /// </summary>
        public PlaceholderManager4KB()
        {
            _mappings = new MappingTree<MappingState>();
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
                _mappings.Add(new RangeNode<MappingState>(address, address + size, MappingState.Unmapped));
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
            ulong startAddress = address;
            ulong unmapSize = size;
            ulong endAddress = startAddress + unmapSize;

            var overlaps = new RangeNode<MappingState>[InitialOverlapsSize];
            int count = 0;

            lock (_mappings)
            {
                count = _mappings.GetNodes(startAddress, endAddress, ref overlaps);
            }

            for (int index = 0; index < count; index++)
            {
                var overlap = overlaps[index];

                lock (_mappings)
                {
                    _mappings.Remove(overlap);
                }

                ulong unmapStart = Math.Max(overlap.Start, startAddress);
                ulong unmapEnd = Math.Min(overlap.End, endAddress);

                if (overlap.Value == MappingState.Mapped)
                {
                    ulong currentAddress = unmapStart;
                    while (currentAddress < unmapEnd)
                    {
                        if (!WindowsApi.UnmapViewOfFile2(WindowsApi.CurrentProcessHandle, (IntPtr)currentAddress, 2))
                        {
                            throw new WindowsApiException("UnmapViewOfFile2");
                        }

                        currentAddress += PageSize;
                    }
                }
            }
        }

        /// <summary>
        /// Unmaps the specified range of memory and marks it as mapped internally.
        /// </summary>
        /// <remarks>
        /// Since this marks the range as mapped, the expectation is that the range will be mapped after calling this method.
        /// </remarks>
        /// <param name="sharedMemory">Shared memory that will be the backing storage for the view</param>
        /// <param name="srcOffset">Offset in the shared memory to map</param>
        /// <param name="location">Address to map the view into</param>
        /// <param name="size">Size of the view in bytes</param>
        public void MapView(IntPtr sharedMemory, ulong srcOffset, IntPtr location, IntPtr size)
        {
            ulong startAddress = (ulong)location;
            ulong unmapSize = (ulong)size;
            ulong endAddress = startAddress + unmapSize;

            var overlaps = new RangeNode<MappingState>[InitialOverlapsSize];
            int count = 0;

            lock (_mappings)
            {
                count = _mappings.GetNodes(startAddress, endAddress, ref overlaps);

                for (int index = 0; index < count; index++)
                {
                    var overlap = overlaps[index];

                    _mappings.Remove(overlap);

                    if (overlap.Start < startAddress)
                    {
                        _mappings.Add(new RangeNode<MappingState>(overlap.Start, startAddress, overlap.Value));
                    }

                    if (overlap.End > endAddress)
                    {
                        _mappings.Add(new RangeNode<MappingState>(endAddress, overlap.End, overlap.Value));
                    }
                }

                RangeNode<MappingState> newNode = new RangeNode<MappingState>(startAddress, endAddress, MappingState.Mapped);
                _mappings.Add(newNode);
            }

            for (int index = 0; index < count; index++)
            {
                var overlap = overlaps[index];

                ulong unmapStart = Math.Max(overlap.Start, startAddress);
                ulong unmapEnd = Math.Min(overlap.End, endAddress);

                ulong currentAddress = unmapStart;
                while (currentAddress < unmapEnd)
                {
                    // If the memory is already mapped, we need to unmap it first, there's
                    // no need to split in this case as it was already split for the map.
                    // If it's unmapped, we need to split.
                    if (overlap.Value == MappingState.Mapped)
                    {
                        if (!WindowsApi.UnmapViewOfFile2(WindowsApi.CurrentProcessHandle, (IntPtr)currentAddress, 2))
                        {
                            throw new WindowsApiException("UnmapViewOfFile2");
                        }
                    }
                    else
                    {
                        bool wasSplitBefore = currentAddress > unmapStart || overlap.Start == unmapStart;
                        bool wasSplitAfter = currentAddress + PageSize == overlap.End;

                        if (!wasSplitBefore || !wasSplitAfter)
                        {
                            CheckFreeResult(WindowsApi.VirtualFree(
                                (IntPtr)currentAddress,
                                (IntPtr)PageSize,
                                AllocationType.Release | AllocationType.PreservePlaceholder));
                        }
                    }

                    var ptr = WindowsApi.MapViewOfFile3(
                        sharedMemory,
                        WindowsApi.CurrentProcessHandle,
                        (IntPtr)currentAddress,
                        srcOffset + (currentAddress - startAddress),
                        (IntPtr)PageSize,
                        0x4000,
                        MemoryProtection.ReadWrite,
                        IntPtr.Zero,
                        0);

                    if (ptr == IntPtr.Zero)
                    {
                        throw new WindowsApiException("MapViewOfFile3");
                    }

                    currentAddress += PageSize;
                }
            }
        }

        /// <summary>
        /// Unmaps views at the specified memory range.
        /// </summary>
        /// <param name="location">Address of the range</param>
        /// <param name="size">Size of the range in bytes</param>
        /// <param name="owner">Memory block that owns the mapping</param>
        public void UnmapView(IntPtr location, IntPtr size, MemoryBlock owner)
        {
            ulong startAddress = (ulong)location;
            ulong unmapSize = (ulong)size;
            ulong endAddress = startAddress + unmapSize;
            ulong coalesceStart;
            ulong coalesceSize;

            var overlaps = new RangeNode<MappingState>[InitialOverlapsSize];
            int count = 0;

            lock (_mappings)
            {
                count = _mappings.GetNodes(startAddress, endAddress, ref overlaps);

                ulong extendedStart = startAddress;
                ulong extendedEnd = endAddress;

                for (int index = 0; index < count; index++)
                {
                    var overlap = overlaps[index];

                    _mappings.Remove(overlap);

                    if (overlap.Start < extendedStart)
                    {
                        if (overlap.Value == MappingState.Unmapped)
                        {
                            extendedStart = overlap.Start;
                        }
                        else
                        {
                            _mappings.Add(new RangeNode<MappingState>(overlap.Start, extendedStart, MappingState.Mapped));
                        }
                    }

                    if (overlap.End > extendedEnd)
                    {
                        if (overlap.Value == MappingState.Unmapped)
                        {
                            extendedEnd = overlap.End;
                        }
                        else
                        {
                            _mappings.Add(new RangeNode<MappingState>(extendedEnd, overlap.End, MappingState.Mapped));
                        }
                    }
                }

                RangeNode<MappingState> newNode = new RangeNode<MappingState>(extendedStart, extendedEnd, MappingState.Unmapped);
                _mappings.Add(newNode);

                newNode = TryCoalesce(newNode, owner);

                coalesceStart = newNode.Start;
                coalesceSize = newNode.End - coalesceStart;
            }

            int unmappedCount = 0;

            for (int index = 0; index < count; index++)
            {
                var overlap = overlaps[index];

                if (overlap.Value == MappingState.Mapped)
                {
                    ulong unmapStart = Math.Max(overlap.Start, startAddress);
                    ulong unmapEnd = Math.Min(overlap.End, endAddress);

                    ulong currentAddress = unmapStart;
                    while (currentAddress < unmapEnd)
                    {
                        if (!WindowsApi.UnmapViewOfFile2(WindowsApi.CurrentProcessHandle, (IntPtr)currentAddress, 2))
                        {
                            throw new WindowsApiException("UnmapViewOfFile2");
                        }

                        currentAddress += PageSize;
                    }

                    unmappedCount += (int)((unmapEnd - unmapStart) / PageSize);
                }
            }

            if (unmappedCount > 1 || (unmappedCount == 1 && coalesceSize > PageSize))
            {
                CheckFreeResult(WindowsApi.VirtualFree(
                    (IntPtr)coalesceStart,
                    (IntPtr)coalesceSize,
                    AllocationType.Release | AllocationType.CoalescePlaceholders));
            }
        }

        private RangeNode<MappingState> TryCoalesce(RangeNode<MappingState> node, MemoryBlock owner)
        {
            // We can only coalesce unmapped nodes, because if it is mapped, then it's
            // not a placeholder and there's no way to coalesce it.
            if (node.Value != MappingState.Unmapped)
            {
                return node;
            }

            RangeNode<MappingState> predecessor = node.Predecessor;
            RangeNode<MappingState> successor = node.Successor;

            ulong blockAddress = (ulong)owner.Pointer;
            ulong blockEnd = blockAddress + owner.Size;

            if (predecessor != null && predecessor.Value == MappingState.Unmapped && predecessor.Start >= blockAddress)
            {
                predecessor.Extend(node.End - node.Start);
                _mappings.Remove(node);
                node = predecessor;
            }

            if (successor != null && successor.Value == MappingState.Unmapped && successor.End <= blockEnd)
            {
                node.Extend(successor.End - successor.Start);
                _mappings.Remove(successor);
            }

            return node;
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
    }
}