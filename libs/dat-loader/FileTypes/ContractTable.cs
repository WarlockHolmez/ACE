using System;
using System.Collections.Generic;
using System.IO;
using ACE.DatLoader.Entity;

namespace ACE.DatLoader.FileTypes;

/// <summary>
/// This is the client_portal.dat file 0x0E00001D
/// </summary>
[DatFileType(DatFileType.ContractTable)]
public class ContractTable : FileType
{
    internal const uint FILE_ID = 0x0E00001D;

    public Dictionary<uint, Contract> Contracts { get; } = new Dictionary<uint, Contract>();

    public override void Unpack(BinaryReader reader)
    {
        Id = reader.ReadUInt32();

        var num_contracts = reader.ReadUInt16();
        var table_size = 
        reader.ReadUInt16(); // We don't need this since C# handles it's own memory
        Console.WriteLine($"[ContractTable] Expecting {num_contracts} contracts, Table Size: {table_size} bytes");
        for (ushort i = 0; i < num_contracts; i++)
        {
            var key = reader.ReadUInt32();

            var value = new Contract();
            value.Unpack(reader);

            Contracts.Add(key, value);
        }
    }
}
