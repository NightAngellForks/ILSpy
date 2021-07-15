﻿// Copyright (c) 2018 Daniel Grunwald
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System.Collections.Generic;
using System.Diagnostics;
using dnlib.DotNet;
using ICSharpCode.Decompiler.Util;

namespace ICSharpCode.Decompiler.TypeSystem.Implementation
{
	sealed class MetadataTypeParameter : AbstractTypeParameter
	{
		readonly MetadataModule module;
		readonly GenericParam handle;

		readonly GenericParamAttributes attr;

		// lazy-loaded:
		IReadOnlyList<IType> constraints;
		ThreeState unmanagedConstraint = ThreeState.Unknown;
		const byte nullabilityNotYetLoaded = 255;
		byte nullabilityConstraint = nullabilityNotYetLoaded;

		public static ITypeParameter[] Create(MetadataModule module, ITypeDefinition copyFromOuter, IEntity owner, IList<GenericParam> handles)
		{
			if (handles.Count == 0)
				return Empty<ITypeParameter>.Array;
			var outerTps = copyFromOuter.TypeParameters;
			var tps = new ITypeParameter[handles.Count];
			int i = 0;
			foreach (var handle in handles) {
				if (i < outerTps.Count)
					tps[i] = outerTps[i];
				else
					tps[i] = Create(module, owner, i, handle);
				i++;
			}
			return tps;
		}

		public static ITypeParameter[] Create(MetadataModule module, IEntity owner, IList<GenericParam> handles)
		{
			if (handles.Count == 0)
				return Empty<ITypeParameter>.Array;
			var tps = new ITypeParameter[handles.Count];
			int i = 0;
			foreach (var handle in handles) {
				tps[i] = Create(module, owner, i, handle);
				i++;
			}
			return tps;
		}

		public static MetadataTypeParameter Create(MetadataModule module, IEntity owner, int index, GenericParam gp)
		{
			Debug.Assert(gp.Number == index);
			return new MetadataTypeParameter(module, owner, index, gp.Name, gp, gp.Flags);
		}

		private MetadataTypeParameter(MetadataModule module, IEntity owner, int index, string name,
			GenericParam handle, GenericParamAttributes attr)
			: base(owner, index, name, GetVariance(attr))
		{
			this.module = module;
			this.handle = handle;
			this.attr = attr;
		}

		private static VarianceModifier GetVariance(GenericParamAttributes attr)
		{
			switch (attr & GenericParamAttributes.VarianceMask) {
				case GenericParamAttributes.Contravariant:
					return VarianceModifier.Contravariant;
				case GenericParamAttributes.Covariant:
					return VarianceModifier.Covariant;
				default:
					return VarianceModifier.Invariant;
			}
		}

		public override IEnumerable<IAttribute> GetAttributes()
		{
			var attributes = handle.CustomAttributes;
			var b = new AttributeListBuilder(module, attributes.Count);
			b.Add(attributes, SymbolKind.TypeParameter);
			return b.Build();
		}

		public override bool HasDefaultConstructorConstraint => (attr & GenericParamAttributes.DefaultConstructorConstraint) != 0;
		public override bool HasReferenceTypeConstraint => (attr & GenericParamAttributes.ReferenceTypeConstraint) != 0;
		public override bool HasValueTypeConstraint => (attr & GenericParamAttributes.NotNullableValueTypeConstraint) != 0;

		public override bool HasUnmanagedConstraint {
			get {
				if (unmanagedConstraint == ThreeState.Unknown) {
					unmanagedConstraint = LoadUnmanagedConstraint().ToThreeState();
				}
				return unmanagedConstraint == ThreeState.True;
			}
		}

		private bool LoadUnmanagedConstraint()
		{
			if ((module.TypeSystemOptions & TypeSystemOptions.UnmanagedConstraints) == 0)
				return false;
			return handle.CustomAttributes.HasKnownAttribute(KnownAttribute.IsUnmanaged);
		}

		public override Nullability NullabilityConstraint {
			get {
				if (nullabilityConstraint == nullabilityNotYetLoaded) {
					nullabilityConstraint = (byte)LoadNullabilityConstraint();
				}
				return (Nullability)nullabilityConstraint;
			}
		}

		Nullability LoadNullabilityConstraint()
		{
			if ((module.TypeSystemOptions & TypeSystemOptions.NullabilityAnnotations) == 0)
				return Nullability.Oblivious;

			foreach (var customAttribute in handle.CustomAttributes) {
				if (customAttribute.IsKnownAttribute(KnownAttribute.Nullable)) {
					if (customAttribute.ConstructorArguments.Count == 1) {
						if (customAttribute.ConstructorArguments[0].Value is byte b && b <= 2) {
							return (Nullability)b;
						}
					}
				}
			}
			return Nullability.Oblivious;
		}

		public override IEnumerable<IType> DirectBaseTypes {
			get {
				var constraints = LazyInit.VolatileRead(ref this.constraints);
				if (constraints != null)
					return constraints;
				return LazyInit.GetOrSet(ref this.constraints, DecodeConstraints());
			}
		}

		private IReadOnlyList<IType> DecodeConstraints()
		{
			var constraintHandleCollection = handle.GenericParamConstraints;
			List<IType> result = new List<IType>(constraintHandleCollection.Count + 1);
			bool hasNonInterfaceConstraint = false;
			foreach (var constraint in constraintHandleCollection) {
				var ty = module.ResolveType(constraint.Constraint, new GenericContext(Owner), constraint);
				result.Add(ty);
				hasNonInterfaceConstraint |= (ty.Kind != TypeKind.Interface);
			}
			if (this.HasValueTypeConstraint) {
				result.Add(Compilation.FindType(KnownTypeCode.ValueType));
			} else if (!hasNonInterfaceConstraint) {
				result.Add(Compilation.FindType(KnownTypeCode.Object));
			}
			return result;
		}

		public override int GetHashCode()
		{
			return 0x51fc5b83 ^ module.PEFile.GetHashCode() ^ handle.GetHashCode();
		}

		public override bool Equals(IType other)
		{
			return other is MetadataTypeParameter tp && handle == tp.handle && module.PEFile == tp.module.PEFile;
		}

		public override string ToString()
		{
			return $"{handle.MDToken.Raw:X8} Index={Index} Owner={Owner}";
		}
	}
}
