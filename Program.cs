namespace BPerfCPUSamplesCollector
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Text;

    class Program
    {
        static void Main(string[] args)
        {
            ReadOnlySpan<byte> bytes = File.ReadAllBytes(@"C:\users\muks\desktop\libcoreclr.dbg");

            ref readonly var elf = ref MemoryMarshal.AsRef<ELFHeader>(bytes);
            if (!ELFHeader.IsValid(in elf))
            {
                Console.WriteLine("Invalid ELF File");
            }

            var sectionHeaders = MemoryMarshal.Cast<byte, ELFSectionHeader>(bytes.Slice((int)elf.SectionHeaderOffset, elf.SectionHeaderCount * elf.SectionHeaderEntrySize));
            var programHeaders = MemoryMarshal.Cast<byte, ELFProgramHeader>(bytes.Slice((int)elf.ProgramHeaderOffset, elf.ProgramHeaderCount * elf.ProgramHeaderEntrySize));

            var nameSection = sectionHeaders[elf.SectionHeaderStringIndex];
            var nameSectionData = bytes.Slice((int)nameSection.FileOffset, (int)nameSection.FileSize);

            var allSections = new Dictionary<string, ELFSectionHeader>();
            var noteSections = new Dictionary<string, ELFSectionHeader>();

            string buildId;
            var dwarfCompilationUnitHeaders = new Dictionary<int, DwarfCompilationUnitHeader>();

            var abbrevDict = new Dictionary<int, Dictionary<int, DwarfAbbrevInfo>>();

            var debugInfoData = new ReadOnlySpan<byte>();

            foreach (var section in sectionHeaders)
            {
                int index = (int)section.NameIndex;
                int count = 0;
                for (; index + count < nameSectionData.Length; ++count)
                {
                    if (nameSectionData[index + count] == 0)
                    {
                        break;
                    }
                }

                var sect = Encoding.ASCII.GetString(nameSectionData.Slice(index, count));
                allSections.Add(sect, section);

                if (section.Type == ELFSectionHeaderType.Note)
                {
                    noteSections.Add(sect, section);

                    if (sect == ".note.gnu.build-id")
                    {
                        var noteData = bytes.Slice((int)section.FileOffset, (int)section.FileSize);

                        ref readonly var elfNote = ref MemoryMarshal.AsRef<ELFNoteHeader>(noteData);

                        if (elfNote.NameSize == 4 && BitConverter.ToInt32(noteData.Slice(Marshal.SizeOf<ELFNoteHeader>(), 4)) == 0x554E47)
                        {
                            var buildIdData = noteData.Slice(Marshal.SizeOf<ELFNoteHeader>() + (int)elfNote.NameSize);
                            var sb = new StringBuilder(40);
                            foreach (var t in buildIdData)
                            {
                                sb.Append($"{t:x}");
                            }

                            buildId = sb.ToString();
                        }
                    }
                }

                if (section.Type == ELFSectionHeaderType.ProgBits && sect == ".debug_info")
                {
                    debugInfoData = bytes.Slice((int)section.FileOffset, (int)section.FileSize);
                    int i = 0;
                    while (i < debugInfoData.Length)
                    {
                        ref readonly DwarfCompilationUnitHeader cuh = ref MemoryMarshal.AsRef<DwarfCompilationUnitHeader>(debugInfoData.Slice(i));
                        var off = i + Marshal.SizeOf<DwarfCompilationUnitHeader>();
                        i += (int)cuh.Size + sizeof(uint);
                        dwarfCompilationUnitHeaders.Add(off, cuh);
                    }
                }

                if (section.Type == ELFSectionHeaderType.ProgBits && sect == ".debug_abbrev")
                {
                    var debugAbbrevData = bytes.Slice((int)section.FileOffset, (int)section.FileSize);
                    int position = 0;

                    while (position < debugAbbrevData.Length)
                    {
                        var p = position;
                        var item = ReadAbbrevs(debugAbbrevData, ref position);
                        abbrevDict.Add(p, item);
                    }
                }
            }

            foreach (var cu in dwarfCompilationUnitHeaders)
            {
                var off = cu.Key;
                var value = cu.Value;
            }

            DumpDebugInfo2(abbrevDict, debugInfoData, dwarfCompilationUnitHeaders);
        }

        private static void DumpDebugInfo(Dictionary<int, Dictionary<int, DwarfAbbrevInfo>> abbrevDict, ReadOnlySpan<byte> debugInfoData, Dictionary<int, DwarfCompilationUnitHeader> dwarfCompilationUnitHeaders)
        {
            foreach (var cu in dwarfCompilationUnitHeaders)
            {
                var off = cu.Key;
                var value = cu.Value;

                Console.WriteLine($"  Compilation Unit @ offset 0x{(off - Marshal.SizeOf<DwarfCompilationUnitHeader>()):x}:");
                Console.WriteLine($"   Length:        0x{value.Size:x} (32-bit)");
                Console.WriteLine($"   Version:       {value.Version}");
                Console.WriteLine($"   Abbref Offset: 0x{value.OffsetIntoAbbrev:x}");
                Console.WriteLine($"   Pointer Size:  {value.PointerSize}");

                var abbrevTable = abbrevDict[(int)value.OffsetIntoAbbrev];

                var end = (int)value.Size + off - Marshal.SizeOf<DwarfCompilationUnitHeader>();

                while (off < end)
                {
                    var printPos = off;
                    var abbrevNumber = (int)ReadLEB128Unsigned(debugInfoData, ref off);

                    // handle null case
                    if (abbrevNumber == 0)
                    {
                        Console.WriteLine($"    <{printPos:x}>: Abbrev Number: 0");
                    }
                    else
                    {
                        var abbrev = abbrevTable[abbrevNumber];

                        Console.WriteLine($"    <{printPos:x}>: Abbrev Number: {abbrev.Number} ({abbrev.Tag})");

                        foreach (var attr in abbrev.Attributes)
                        {
                            Console.WriteLine($"    <{off:x}>   {attr.Name}\t: Value");
                            ReadAttributeValue(debugInfoData, attr.Form, ref off);
                        }
                    }
                }
            }
        }

        private static void DumpDebugInfo2(Dictionary<int, Dictionary<int, DwarfAbbrevInfo>> abbrevDict, ReadOnlySpan<byte> debugInfoData, Dictionary<int, DwarfCompilationUnitHeader> dwarfCompilationUnitHeaders)
        {
            foreach (var cu in dwarfCompilationUnitHeaders)
            {
                var off = cu.Key;
                var value = cu.Value;

                Console.WriteLine($"  Compilation Unit @ offset 0x{(off - Marshal.SizeOf<DwarfCompilationUnitHeader>()):x}:");
                Console.WriteLine($"   Length:        0x{value.Size:x} (32-bit)");
                Console.WriteLine($"   Version:       {value.Version}");
                Console.WriteLine($"   Abbref Offset: 0x{value.OffsetIntoAbbrev:x}");
                Console.WriteLine($"   Pointer Size:  {value.PointerSize}");

                var end = (int)value.Size + off - Marshal.SizeOf<DwarfCompilationUnitHeader>();

                int nestingLevel = 1;
                var abbrevTable = abbrevDict[(int)value.OffsetIntoAbbrev];

                while (off < end)
                {
                    var printPos = off;

                    var abbrevNumber = (int)ReadLEB128Unsigned(debugInfoData, ref off);

                    // handle null case
                    if (abbrevNumber == 0)
                    {
                        Console.WriteLine($"    <{printPos:x}>: Abbrev Number: 0");

                        --nestingLevel;
                    }
                    else
                    {
                        var abbrev = abbrevTable[abbrevNumber];

                        Console.WriteLine($"    <{printPos:x}>: Abbrev Number: {abbrev.Number} ({abbrev.Tag})");

                        switch (abbrev.Tag)
                        {
                            case DwarfTag.DW_TAG_subprogram:
                            case DwarfTag.DW_TAG_entry_point:
                            case DwarfTag.DW_TAG_inlined_subroutine:
                            {
                                foreach (var attribute in abbrev.Attributes)
                                {
                                    switch (attribute.Name)
                                    {
                                        case DwarfAttribute.DW_AT_low_pc:
                                            break;
                                        case DwarfAttribute.DW_AT_high_pc:
                                            break;
                                        case DwarfAttribute.DW_AT_ranges:
                                            break;
                                    }
                                }

                                break;
                            }
                        }

                        if (abbrev.HasChildren)
                        {
                            ++nestingLevel;
                        }

                        foreach (var attr in abbrev.Attributes)
                        {
                            Console.WriteLine($"    <{off:x}>   {attr.Name}\t: Value");
                            ReadAttributeValue(debugInfoData, attr.Form, ref off);
                        }
                    }
                }

                /*

                var end = (int)value.Size + off - Marshal.SizeOf<DwarfCompilationUnitHeader>();


                while (off < end)
                {
                    var printPos = off;
                    var abbrevNumber = (int)ReadLEB128Unsigned(debugInfoData, ref off);

                    // handle null case
                    if (abbrevNumber == 0)
                    {
                        Console.WriteLine($"    <{printPos:x}>: Abbrev Number: 0");
                    }
                    else
                    {
                        var abbrev = abbrevTable[abbrevNumber];

                        Console.WriteLine($"    <{printPos:x}>: Abbrev Number: {abbrev.Number} ({abbrev.Tag})");

                        foreach (var attr in abbrev.Attributes)
                        {
                            Console.WriteLine($"    <{off:x}>   {attr.Name}\t: Value");
                            ReadAttributeValue(debugInfoData, attr.Form, ref off);
                        }
                    }
                }*/
            }
        }

        private static Dictionary<int, DwarfAbbrevInfo> ReadAbbrevs(ReadOnlySpan<byte> abbrevData, ref int position)
        {
            var dict = new Dictionary<int, DwarfAbbrevInfo>();
            
            uint abbrevNumber = ReadLEB128Unsigned(abbrevData, ref position);

            while (abbrevNumber != 0)
            {
                var info = new DwarfAbbrevInfo
                {
                    Number = abbrevNumber,
                    Tag = (DwarfTag)ReadLEB128Unsigned(abbrevData, ref position),
                    HasChildren = abbrevData[position] == 1,
                    Attributes = new List<DwarfAbbrevAttribute>()
                };

                position++;

                var attributeName = ReadLEB128Unsigned(abbrevData, ref position);
                var attributeForm = ReadLEB128Unsigned(abbrevData, ref position);

                while (attributeName != 0)
                {
                    info.Attributes.Add(new DwarfAbbrevAttribute { Name = (DwarfAttribute)attributeName, Form = (DwarfForm)attributeForm });
                    attributeName = ReadLEB128Unsigned(abbrevData, ref position);
                    attributeForm = ReadLEB128Unsigned(abbrevData, ref position);
                }

                dict.Add((int)abbrevNumber, info);
                abbrevNumber = ReadLEB128Unsigned(abbrevData, ref position);
            }

            return dict;
        }

        private static byte[] ReadAttributeValue(ReadOnlySpan<byte> data, DwarfForm form, ref int position)
        {
            switch (form)
            {
                case DwarfForm.DW_FORM_ref_addr:
                {
                    position += 4;
                    return Array.Empty<byte>();
                }
                case DwarfForm.DW_FORM_addr: // only support ELF64
                {
                    var retval = new byte[8];
                    data.Slice(position, 8).CopyTo(retval);
                    position += 8;

                    return retval;
                }
                case DwarfForm.DW_FORM_block1:
                {
                    var len = data[position];
                    position += 1;
                    position += len;

                    return Array.Empty<byte>();
                }
                case DwarfForm.DW_FORM_block2:
                {
                    var len = BitConverter.ToUInt16(data.Slice(position, 2));
                    position += 2;
                    position += len;

                    return Array.Empty<byte>();
                }
                case DwarfForm.DW_FORM_block4:
                {
                    var len = (int)BitConverter.ToUInt32(data.Slice(position, 4));
                    position += 4;
                    position += len;

                    return Array.Empty<byte>();
                }
                case DwarfForm.DW_FORM_block:
                case DwarfForm.DW_FORM_exprloc:
                {
                    var len = (int)ReadLEB128Unsigned(data, ref position);
                    position += len;

                    return Array.Empty<byte>();
                }
                case DwarfForm.DW_FORM_strp:
                case DwarfForm.DW_FORM_sec_offset:
                {
                    var offset = (int)BitConverter.ToUInt32(data.Slice(position, 4));
                    position += 4;

                    return Array.Empty<byte>();
                }
                case DwarfForm.DW_FORM_ref1:
                case DwarfForm.DW_FORM_data1:
                case DwarfForm.DW_FORM_flag:
                {
                    position += 1;
                    return Array.Empty<byte>();
                }
                case DwarfForm.DW_FORM_ref2:
                case DwarfForm.DW_FORM_data2:
                {
                    position += 2;
                    return Array.Empty<byte>();
                }
                case DwarfForm.DW_FORM_ref4:
                case DwarfForm.DW_FORM_data4:
                {
                    position += 4;
                    return Array.Empty<byte>();
                }
                case DwarfForm.DW_FORM_ref8:
                case DwarfForm.DW_FORM_ref_sig8:
                case DwarfForm.DW_FORM_data8:
                {
                    position += 8;
                    return Array.Empty<byte>();
                }
                case DwarfForm.DW_FORM_udata:
                case DwarfForm.DW_FORM_ref_udata:
                {
                    var value = (int)ReadLEB128Unsigned(data, ref position);

                    return Array.Empty<byte>();
                }
                case DwarfForm.DW_FORM_flag_present:
                {
                    return Array.Empty<byte>();
                }
                case DwarfForm.DW_FORM_indirect:
                {
                    var indirectForm = (DwarfForm)(int)ReadLEB128Unsigned(data, ref position);
                    ReadAttributeValue(data, indirectForm, ref position);

                    return Array.Empty<byte>();
                }
                case DwarfForm.DW_FORM_string:
                {
                    // TODO: Implement
                    return Array.Empty<byte>();
                }
                case DwarfForm.DW_FORM_sdata:
                {
                    var value = ReadLEB128Signed(data, ref position);

                    return Array.Empty<byte>();
                }
                default:
                    throw new NotImplementedException();
            }
        }

        private static uint ReadLEB128Unsigned(ReadOnlySpan<byte> data, ref int position)
        {
            int shift = 0;
            uint result = 0;

            while (true)
            {
                byte b = data[position];
                position++;

                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    break;
                }

                shift += 7;
            }

            return result;
        }

        private static int ReadLEB128Signed(ReadOnlySpan<byte> data, ref int position)
        {
            int result = 0;
            int shift = 0;

            while (true)
            {
                byte b = data[position];
                position++;

                result |= (b & 0x7F) << shift;
                shift += 7;

                if ((0x80 & b) == 0)
                {
                    if (shift < 32 && (b & 0x40) != 0)
                    {
                        return result | (~0 << shift);
                    }

                    return result;
                }
            }
        }
    }
}
