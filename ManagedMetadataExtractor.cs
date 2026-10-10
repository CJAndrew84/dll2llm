using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using static DllToLLMDoc.SymbolCatalog;

namespace DllToLLMDoc;

// Reads ECMA-335 tables, not Assembly.Load/LoadFrom: target code and dependency initializers never run.
internal static class ManagedMetadataExtractor
{
    internal static Extraction Extract(string path, string source, bool includeNonPublic = false)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) throw new InvalidDataException("Native PE: no CLR metadata. Use the native metadata adapter; headers/PDB analysis is not implemented here.");
        var r = pe.GetMetadataReader();
        var a = r.IsAssembly ? r.GetAssemblyDefinition() : default;
        var assembly = r.IsAssembly ? r.GetString(a.Name) + ", Version=" + a.Version : r.GetString(r.GetModuleDefinition().Name);
        var provider = new TypeNames();
        var symbols = new List<Symbol>();
        var diagnostics = new List<string>();
        var assemblyReferences = r.AssemblyReferences.Select(h =>
        {
            var ar = r.GetAssemblyReference(h);
            return r.GetString(ar.Name) + ", Version=" + ar.Version;
        }).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        string[] Constraints(GenericParameterHandleCollection handles) =>
            handles.SelectMany(h =>
            {
                var p = r.GetGenericParameter(h);
                return p.GetConstraints().Select(c => r.GetGenericParameterConstraint(c))
                    .Select(c => r.GetString(p.Name) + " : " + TypeName(c.Type, new Context([], [])))
                    .Concat(new[] { r.GetString(p.Name) + " flags: " + p.Attributes });
            }).ToArray();
        string[] AttributeTokens(CustomAttributeHandleCollection handles) =>
            handles.Select(h =>
            {
                var attribute = r.GetCustomAttribute(h);
                string owner = attribute.Constructor.Kind switch
                {
                    HandleKind.MethodDefinition => provider.GetTypeFromDefinition(r,
                        r.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(), 0),
                    HandleKind.MemberReference => AttributeOwner((MemberReferenceHandle)attribute.Constructor),
                    _ => "unknown-constructor"
                };
                return owner + " [token:0x" + MetadataTokens.GetToken(h).ToString("X8") + "]";
            }).ToArray();
        string AttributeOwner(MemberReferenceHandle handle)
        {
            var member = r.GetMemberReference(handle);
            return member.Parent.Kind switch
            {
                HandleKind.TypeReference => provider.GetTypeFromReference(r, (TypeReferenceHandle)member.Parent, 0),
                HandleKind.TypeDefinition => provider.GetTypeFromDefinition(r, (TypeDefinitionHandle)member.Parent, 0),
                HandleKind.TypeSpecification => provider.GetTypeFromSpecification(r, new Context([], []), (TypeSpecificationHandle)member.Parent, 0),
                _ => "unknown-parent"
            };
        }

        foreach (var handle in r.TypeDefinitions)
        {
            var t = r.GetTypeDefinition(handle);
            if (r.GetString(t.Name) == "<Module>" || (!includeNonPublic && !TypeVisible(handle))) continue;
            var full = provider.GetTypeFromDefinition(r, handle, 0);
            var ns = Namespace(handle);
            var ctx = new Context(GenericNames(t.GetGenericParameters()), []);
            var baseType = t.BaseType.IsNil ? null : TypeName(t.BaseType, ctx);
            var kind = (t.Attributes & TypeAttributes.Interface) != 0 ? "interface" : baseType switch
            { "System.Enum" => "enum", "System.ValueType" => "struct", "System.MulticastDelegate" => "delegate", _ => "class" };
            Add(() => Make(handle, r.GetString(t.Name), kind, null, full, null) with
            {
                Namespace = ns, Visibility = (t.Attributes & TypeAttributes.VisibilityMask).ToString(), Attributes = t.Attributes.ToString(),
                BaseType = baseType, Interfaces = t.GetInterfaceImplementations().Select(x => TypeName(r.GetInterfaceImplementation(x).Interface, ctx)).ToArray(),
                GenericParameters = ctx.TypeParameters.ToArray(), GenericConstraints = Constraints(t.GetGenericParameters()),
                CustomAttributes = AttributeTokens(t.GetCustomAttributes()), AssemblyReferences = assemblyReferences
            });
            foreach (var mh in t.GetMethods())
            {
                var m = r.GetMethodDefinition(mh);
                if (!includeNonPublic && !MethodVisible(mh)) continue;
                Add(() =>
                {
                    var mc = ctx with { MethodParameters = GenericNames(m.GetGenericParameters()) };
                    var sig = m.DecodeSignature(provider, mc);
                    var name = r.GetString(m.Name);
                    var rows = m.GetParameters().Select(p => r.GetParameter(p)).Where(p => p.SequenceNumber > 0).ToDictionary(p => (int)p.SequenceNumber);
                    var parameters = sig.ParameterTypes.Select((pt, i) =>
                    {
                        var exists = rows.TryGetValue(i + 1, out var row);
                        return new Parameter(i + 1, exists && !row.Name.IsNil ? r.GetString(row.Name) : "arg" + i,
                            pt, exists ? row.Attributes.ToString() : null, exists ? ConstantValue(row.GetDefaultValue()) : null);
                    }).ToArray();
                    var generic = mc.MethodParameters.Length == 0 ? "" : "<" + string.Join(", ", mc.MethodParameters) + ">";
                    var display = sig.ReturnType + " " + name + generic + "(" + string.Join(", ", parameters.Select(p => p.Type + " " + p.Name)) + ")";
                    return Make(mh, name, name is ".ctor" or ".cctor" ? "constructor" : "method", full, full + "." + name, display) with
                    {
                        Namespace = ns, Visibility = (m.Attributes & MethodAttributes.MemberAccessMask).ToString(), Attributes = m.Attributes.ToString(),
                        ReturnType = sig.ReturnType, Parameters = parameters, GenericParameters = mc.MethodParameters.ToArray(), SignatureHex = Hex(m.Signature), GenericConstraints = Constraints(m.GetGenericParameters()),
                        CustomAttributes = AttributeTokens(m.GetCustomAttributes())
                    };
                });
            }
            foreach (var ph in t.GetProperties())
            {
                var p = r.GetPropertyDefinition(ph);
                var access = p.GetAccessors();
                if (!includeNonPublic && !MethodVisible(access.Getter) && !MethodVisible(access.Setter)) continue;
                Add(() =>
                {
                    var sig = p.DecodeSignature(provider, ctx);
                    var name = r.GetString(p.Name);
                    var acc = new[] { Accessor("get", access.Getter), Accessor("set", access.Setter) }.Where(x => x != null).Cast<string>().ToArray();
                    return Make(ph, name, "property", full, full + "." + name,
                        sig.ReturnType + " " + name + (sig.ParameterTypes.Length > 0 ? "[" + string.Join(", ", sig.ParameterTypes) + "]" : "") + " { " + string.Join("; ", acc) + "; }") with
                    {
                        Namespace = ns, ReturnType = sig.ReturnType, Attributes = p.Attributes.ToString(), SignatureHex = Hex(p.Signature), Accessors = acc,
                        Parameters = sig.ParameterTypes.Select((x, i) => new Parameter(i + 1, "index" + i, x, null)).ToArray(), Constant = ConstantValue(p.GetDefaultValue()),
                        CustomAttributes = AttributeTokens(p.GetCustomAttributes())
                    };
                });
            }
            foreach (var fh in t.GetFields())
            {
                var f = r.GetFieldDefinition(fh);
                if (!includeNonPublic && !FieldVisible(f.Attributes)) continue;
                Add(() =>
                {
                    var type = f.DecodeSignature(provider, ctx);
                    var name = r.GetString(f.Name);
                    return Make(fh, name, "field", full, full + "." + name, type + " " + name) with
                    { Namespace = ns, ReturnType = type, Visibility = (f.Attributes & FieldAttributes.FieldAccessMask).ToString(), Attributes = f.Attributes.ToString(), SignatureHex = Hex(f.Signature), Constant = ConstantValue(f.GetDefaultValue()), CustomAttributes = AttributeTokens(f.GetCustomAttributes()) };
                });
            }
            foreach (var eh in t.GetEvents())
            {
                var e = r.GetEventDefinition(eh);
                var access = e.GetAccessors();
                if (!includeNonPublic && !MethodVisible(access.Adder) && !MethodVisible(access.Remover)) continue;
                Add(() =>
                {
                    var name = r.GetString(e.Name);
                    var type = TypeName(e.Type, ctx);
                    return Make(eh, name, "event", full, full + "." + name, type + " " + name) with
                    { Namespace = ns, ReturnType = type, Attributes = e.Attributes.ToString(), Accessors = new[] { Accessor("add", access.Adder), Accessor("remove", access.Remover), Accessor("raise", access.Raiser) }.Where(x => x != null).Cast<string>().ToArray(),
                        CustomAttributes = AttributeTokens(e.GetCustomAttributes()) };
                });
            }
        }
        return new Extraction("clr-metadata-v1", assembly, symbols, diagnostics);
        void Add(Func<Symbol> build)
        {
            try { symbols.Add(build()); }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException)
            { diagnostics.Add(ex.GetType().Name + ": " + ex.Message); }
        }
        Symbol Make(EntityHandle h, string name, string kind, string? container, string fullName, string? signature) =>
            new(Identity(source, assembly, fullName, kind, signature), name, kind, container, source, "clr-metadata", signature)
            { FullName = fullName, Assembly = assembly, SourcePointer = "token:0x" + MetadataTokens.GetToken(h).ToString("X8") };
        string[] GenericNames(GenericParameterHandleCollection handles) => handles.Select(h => r.GetString(r.GetGenericParameter(h).Name)).ToArray();
        string Hex(BlobHandle h) => Convert.ToHexString(r.GetBlobBytes(h));
        string? ConstantValue(ConstantHandle h) => h.IsNil ? null : r.GetConstant(h).TypeCode + ":0x" + Hex(r.GetConstant(h).Value);
        string Namespace(TypeDefinitionHandle h)
        {
            var t = r.GetTypeDefinition(h);
            return !t.GetDeclaringType().IsNil ? Namespace(t.GetDeclaringType()) : r.GetString(t.Namespace);
        }
        string TypeName(EntityHandle h, Context c) => h.Kind switch
        {
            HandleKind.TypeDefinition => provider.GetTypeFromDefinition(r, (TypeDefinitionHandle)h, 0),
            HandleKind.TypeReference => provider.GetTypeFromReference(r, (TypeReferenceHandle)h, 0),
            HandleKind.TypeSpecification => provider.GetTypeFromSpecification(r, c, (TypeSpecificationHandle)h, 0),
            _ => throw new BadImageFormatException("Unsupported type handle: " + h.Kind)
        };
        bool TypeVisible(TypeDefinitionHandle h)
        {
            var td = r.GetTypeDefinition(h);
            var v = td.Attributes & TypeAttributes.VisibilityMask;
            return td.GetDeclaringType().IsNil ? v == TypeAttributes.Public :
                (v is TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem) && TypeVisible(td.GetDeclaringType());
        }
        bool MethodVisible(MethodDefinitionHandle h) => !h.IsNil &&
            (r.GetMethodDefinition(h).Attributes & MethodAttributes.MemberAccessMask) is MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem;
        string? Accessor(string label, MethodDefinitionHandle h) => h.IsNil ? null : label + ":" + (r.GetMethodDefinition(h).Attributes & MethodAttributes.MemberAccessMask);
    }
    private static bool FieldVisible(FieldAttributes a) => (a & FieldAttributes.FieldAccessMask) is FieldAttributes.Public or FieldAttributes.Family or FieldAttributes.FamORAssem;
    private sealed record Context(string[] TypeParameters, string[] MethodParameters);
    private sealed class TypeNames : ISignatureTypeProvider<string, Context>
    {
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr[" + s.Header.CallingConvention + "](" + string.Join(", ", s.ParameterTypes) + ")->" + s.ReturnType;
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> args) => genericType + "<" + string.Join(", ", args) + ">";
        public string GetGenericMethodParameter(Context c, int i) => i < c.MethodParameters.Length ? c.MethodParameters[i] : "!!" + i;
        public string GetGenericTypeParameter(Context c, int i) => i < c.TypeParameters.Length ? c.TypeParameters[i] : "!" + i;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType + (isRequired ? " modreq(" : " modopt(") + modifier + ")";
        public string GetPinnedType(string elementType) => elementType + " pinned";
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "System." + typeCode;
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte rawTypeKind)
        {
            var t = r.GetTypeDefinition(h);
            var parent = t.GetDeclaringType();
            return parent.IsNil ? Qualified(r.GetString(t.Namespace), r.GetString(t.Name)) : GetTypeFromDefinition(r, parent, rawTypeKind) + "+" + r.GetString(t.Name);
        }
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte rawTypeKind)
        {
            var t = r.GetTypeReference(h);
            return t.ResolutionScope.Kind == HandleKind.TypeReference ? GetTypeFromReference(r, (TypeReferenceHandle)t.ResolutionScope, rawTypeKind) + "+" + r.GetString(t.Name) : Qualified(r.GetString(t.Namespace), r.GetString(t.Name));
        }
        public string GetTypeFromSpecification(MetadataReader r, Context c, TypeSpecificationHandle h, byte rawTypeKind) => r.GetTypeSpecification(h).DecodeSignature(this, c);
        private static string Qualified(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;
    }
}
