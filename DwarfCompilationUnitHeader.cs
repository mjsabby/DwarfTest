// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace BPerfCPUSamplesCollector
{
    using System.Runtime.InteropServices;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct DwarfCompilationUnitHeader
    {
        public uint Size;
        public ushort Version;
        public uint OffsetIntoAbbrev;
        public byte PointerSize;
    }
}
