using System;
using System.Collections.Immutable;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

// 读出被编译程序集里 MemberRef / MethodDef 的签名，用来判定：
//  1) sts2_lan_connect.dll 按几参的 NetHostGameService.DisconnectClient 编译；
//  2) ConnectedPeers 的元素类型 token 是否与 0.113 一致；
//  3) LanConnectTailMessageRuntime.BindClient 的形参是否含 ReadOnlySpan<byte>（反射不可调用）。
// 用法： memberref-probe <assembly> [--defs TypeName] [filter...]

internal static class Program
{
    private static MetadataReader _reader = null!;

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("usage: memberref-probe <assembly> [--defs <typeFullName>] [member-filter...]");
            return 2;
        }

        string path = args[0];
        string? defsType = null;
        List<string> filters = new();

        for (int index = 1; index < args.Length; index++)
        {
            if (args[index] == "--defs" && index + 1 < args.Length)
            {
                defsType = args[++index];
                continue;
            }

            filters.Add(args[index]);
        }

        using FileStream stream = File.OpenRead(path);
        using PEReader pe = new(stream);
        _reader = pe.GetMetadataReader();

        Console.WriteLine($"assembly: {path}");
        Console.WriteLine($"  mvid: {_reader.GetGuid(_reader.GetModuleDefinition().Mvid)}");

        if (defsType != null)
        {
            DumpTypeDefs(defsType);
            return 0;
        }

        string[] effectiveFilters = filters.Count > 0
            ? filters.ToArray()
            : new[] { "DisconnectClient", "get_ConnectedPeers" };

        int total = 0;
        foreach (MemberReferenceHandle handle in _reader.MemberReferences)
        {
            MemberReference reference = _reader.GetMemberReference(handle);
            string name = _reader.GetString(reference.Name);
            if (Array.IndexOf(effectiveFilters, name) < 0)
            {
                continue;
            }

            total++;
            Console.WriteLine(
                $"  MEMBERREF {DescribeParent(reference.Parent)}::{name} {DescribeSignature(reference.Signature)}");
        }

        Console.WriteLine($"  matches: {total}");

        // 本工具以前在 matches: 0 时也返回 0，于是「要找的成员签名根本不存在」和「一切正常」
        // 看起来一模一样。探针被要求找成员却一个都没找到，就是异常。
        if (total == 0)
        {
            Console.WriteLine(
                "FAIL: no MemberRef matched the requested filter(s) [" + string.Join(", ", effectiveFilters) +
                "]; either the assembly does not contain them or the filter is wrong.");
            return 3;
        }

        return 0;
    }

    private static void DumpTypeDefs(string typeName)
    {
        foreach (TypeDefinitionHandle handle in _reader.TypeDefinitions)
        {
            TypeDefinition type = _reader.GetTypeDefinition(handle);
            string fullName = Combine(
                _reader.GetString(type.Namespace),
                _reader.GetString(type.Name));
            if (!string.Equals(fullName, typeName, StringComparison.Ordinal))
            {
                continue;
            }

            Console.WriteLine($"  TYPE {fullName}");
            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = _reader.GetMethodDefinition(methodHandle);
                string name = _reader.GetString(method.Name);
                if (name is ".ctor" or ".cctor")
                {
                    continue;
                }

                Console.WriteLine($"    METHODDEF {name} {DescribeSignature(method.Signature)}");
            }

            return;
        }

        Console.WriteLine($"  TYPE {typeName} NOT FOUND");
    }

    private static string Combine(string ns, string name) =>
        string.IsNullOrEmpty(ns) ? name : ns + "." + name;

    private static string DescribeParent(EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeReference => _reader.GetString(_reader.GetTypeReference((TypeReferenceHandle)parent).Name),
        HandleKind.TypeDefinition => _reader.GetString(_reader.GetTypeDefinition((TypeDefinitionHandle)parent).Name),
        HandleKind.TypeSpecification => "<typespec>",
        _ => parent.Kind.ToString()
    };

    private static string DescribeSignature(BlobHandle handle)
    {
        BlobReader blob = _reader.GetBlobReader(handle);
        byte callingConvention = blob.ReadByte();
        int genericCount = 0;
        if ((callingConvention & 0x10) != 0)
        {
            genericCount = blob.ReadCompressedInteger();
        }

        int parameterCount = blob.ReadCompressedInteger();
        string returnType = ReadType(ref blob);
        StringBuilder parameters = new();
        for (int index = 0; index < parameterCount; index++)
        {
            if (index > 0)
            {
                parameters.Append(", ");
            }

            parameters.Append(ReadType(ref blob));
        }

        string generics = genericCount > 0 ? $"`{genericCount}" : string.Empty;
        return $"({parameters}) -> {returnType}   [params={parameterCount}, cc=0x{callingConvention:X2}{generics}]";
    }

    private static string ReadType(ref BlobReader blob)
    {
        byte code = blob.ReadByte();
        switch (code)
        {
            case 0x01: return "void";
            case 0x02: return "bool";
            case 0x03: return "char";
            case 0x04: return "int8";
            case 0x05: return "uint8";
            case 0x06: return "int16";
            case 0x07: return "uint16";
            case 0x08: return "int32";
            case 0x09: return "uint32";
            case 0x0a: return "int64";
            case 0x0b: return "uint64";
            case 0x0c: return "float32";
            case 0x0d: return "float64";
            case 0x0e: return "string";
            case 0x0f: return "ptr " + ReadType(ref blob);
            case 0x10: return "byref " + ReadType(ref blob);
            case 0x11: return "valuetype " + ResolveTypeDefOrRef(ref blob);
            case 0x12: return "class " + ResolveTypeDefOrRef(ref blob);
            case 0x13: return "var " + blob.ReadCompressedInteger();
            case 0x14: return "array " + ReadType(ref blob);
            case 0x15: return "genericinst " + ReadGenericInstance(ref blob);
            case 0x18: return "intptr";
            case 0x19: return "uintptr";
            case 0x1b: return "fnptr";
            case 0x1c: return "object";
            case 0x1d: return "szarray " + ReadType(ref blob);
            case 0x1e: return "mvar " + blob.ReadCompressedInteger();
            case 0x1f: return "cmod_reqd " + ReadType(ref blob);
            case 0x20: return "cmod_opt " + ReadType(ref blob);
            case 0x45: return "pinned " + ReadType(ref blob);
            default: return $"<0x{code:X2}>";
        }
    }

    // TypeDefOrRefOrSpecEncoded: 低 2 位 tag（0=TypeDef, 1=TypeRef, 2=TypeSpec）
    private static string ResolveTypeDefOrRef(ref BlobReader blob)
    {
        int coded = blob.ReadCompressedInteger();
        int tag = coded & 0x3;
        int row = coded >> 2;
        switch (tag)
        {
            case 0:
            {
                TypeDefinitionHandle handle = MetadataTokens.TypeDefinitionHandle(row);
                if (handle.IsNil || row > _reader.TypeDefinitions.Count)
                {
                    return $"<typedef row {row}>";
                }

                TypeDefinition type = _reader.GetTypeDefinition(handle);
                return "[" + Combine(_reader.GetString(type.Namespace), _reader.GetString(type.Name)) + "]";
            }

            case 1:
            {
                TypeReferenceHandle handle = MetadataTokens.TypeReferenceHandle(row);
                if (handle.IsNil || row > _reader.TypeReferences.Count)
                {
                    return $"<typeref row {row}>";
                }

                TypeReference type = _reader.GetTypeReference(handle);
                return "[" + Combine(_reader.GetString(type.Namespace), _reader.GetString(type.Name)) + "]";
            }

            case 2:
                return $"<typespec row {row}>";

            default:
                return $"<coded {coded}>";
        }
    }

    private static string ReadGenericInstance(ref BlobReader blob)
    {
        string head = ReadType(ref blob);
        int count = blob.ReadCompressedInteger();
        StringBuilder builder = new();
        for (int index = 0; index < count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append(ReadType(ref blob));
        }

        return head + "<" + builder + ">";
    }
}
