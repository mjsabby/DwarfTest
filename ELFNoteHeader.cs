// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace BPerfCPUSamplesCollector
{
    internal enum ELFNoteType
    {
        GnuBuildId = 3,
        NtFile = 0x46494c45,
    }

    internal struct ELFNoteHeader
    {
        public uint NameSize;
        public uint ContentSize;
        public ELFNoteType Type;
    }
}
