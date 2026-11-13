using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Sts2LobbyEnhance.Tests;

/// <summary>
/// Signature decoder that turns metadata blobs into <see cref="Type"/> instances of the running
/// runtime. Used to name the declaring type of a member reference whose parent is a
/// <c>TypeSpecification</c> (for example <c>List&lt;Player&gt;</c>).
///
/// When a generic argument cannot be resolved (it is a generic parameter of the enclosing type, or a
/// function pointer), the *open* generic definition is returned instead. That is intentional: the
/// caller only needs a type it can search for a member name, and the open definition is the most
/// specific type available without a generic context.
/// </summary>
internal sealed class TypeResolverProvider(Module module) : ISignatureTypeProvider<Type?, object?>
{
    public Type? GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => typeof(bool),
        PrimitiveTypeCode.Byte => typeof(byte),
        PrimitiveTypeCode.SByte => typeof(sbyte),
        PrimitiveTypeCode.Char => typeof(char),
        PrimitiveTypeCode.Int16 => typeof(short),
        PrimitiveTypeCode.UInt16 => typeof(ushort),
        PrimitiveTypeCode.Int32 => typeof(int),
        PrimitiveTypeCode.UInt32 => typeof(uint),
        PrimitiveTypeCode.Int64 => typeof(long),
        PrimitiveTypeCode.UInt64 => typeof(ulong),
        PrimitiveTypeCode.Single => typeof(float),
        PrimitiveTypeCode.Double => typeof(double),
        PrimitiveTypeCode.IntPtr => typeof(IntPtr),
        PrimitiveTypeCode.UIntPtr => typeof(UIntPtr),
        PrimitiveTypeCode.Object => typeof(object),
        PrimitiveTypeCode.String => typeof(string),
        PrimitiveTypeCode.TypedReference => typeof(TypedReference),
        PrimitiveTypeCode.Void => typeof(void),
        _ => null
    };

    public Type? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        Resolve(handle);

    public Type? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
        Resolve(handle);

    public Type? GetTypeFromSpecification(
        MetadataReader reader,
        object? genericContext,
        TypeSpecificationHandle handle,
        byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public Type? GetSZArrayType(Type? elementType) => elementType?.MakeArrayType();

    public Type? GetArrayType(Type? elementType, ArrayShape shape) =>
        elementType?.MakeArrayType(Math.Max(shape.Rank, 1));

    public Type? GetByReferenceType(Type? elementType) => elementType?.MakeByRefType();

    public Type? GetPointerType(Type? elementType) => elementType?.MakePointerType();

    public Type? GetGenericInstantiation(Type? genericType, ImmutableArray<Type?> typeArguments)
    {
        if (genericType is null)
        {
            return null;
        }

        if (typeArguments.Any(argument => argument is null))
        {
            // Best effort: the member lives on the open definition as far as this probe can tell.
            return genericType;
        }

        try
        {
            return genericType.MakeGenericType([.. typeArguments!]);
        }
        catch (ArgumentException)
        {
            // Constraint violation: the open definition is still enough for a name lookup.
            return genericType;
        }
    }

    public Type? GetGenericMethodParameter(object? genericContext, int index) => null;

    public Type? GetGenericTypeParameter(object? genericContext, int index) => null;

    public Type? GetModifiedType(Type? modifier, Type? unmodifiedType, bool isRequired) => unmodifiedType;

    public Type? GetPinnedType(Type? elementType) => elementType;

    public Type? GetFunctionPointerType(MethodSignature<Type?> signature) => null;

    private Type? Resolve(EntityHandle handle)
    {
        try
        {
            return module.ResolveType(MetadataTokens.GetToken(handle));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (TypeLoadException)
        {
            return null;
        }
    }
}
