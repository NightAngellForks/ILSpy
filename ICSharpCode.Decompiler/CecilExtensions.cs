﻿// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
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

using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace ICSharpCode.Decompiler
{
	/// <summary>
	/// Cecil helper methods.
	/// </summary>
	public static class CecilExtensions
	{
		#region GetPushDelta / GetPopDelta
		public static int GetPushDelta(this Instruction instruction)
		{
			instruction.CalculateStackUsage(out int push, out int _);
			return push;
		}

		public static int? GetPopDelta(this Instruction instruction, MethodDef methodDef)
		{
			instruction.CalculateStackUsage(methodDef.HasReturnType, out int _, out int pops);
			return pops;
		}

		/// <summary>
		/// checks if the given TypeReference is one of the following types:
		/// [sbyte, short, int, long, IntPtr]
		/// </summary>
		public static bool IsSignedIntegralType(this TypeSig type)
		{
			if (type == null)
				return false;
			return type.ElementType == ElementType.I1 ||
				   type.ElementType == ElementType.I2 ||
				   type.ElementType == ElementType.I4 ||
				   type.ElementType == ElementType.I8 ||
				   type.ElementType == ElementType.I;
		}

		#endregion

		/// <summary>
		/// Gets the (exclusive) end offset of this instruction.
		/// </summary>
		public static int GetEndOffset(this Instruction inst)
		{
			if (inst == null)
				throw new ArgumentNullException(nameof(inst));
			return (int)inst.Offset + inst.GetSize();
		}

		public static string OffsetToString(int offset)
		{
			return string.Format("IL_{0:x4}", offset);
		}

		public static string OffsetToString(long offset)
		{
			return string.Format("IL_{0:x4}", offset);
		}

		public static HashSet<MethodDef> GetAccessorMethods(this TypeDef type)
		{
			HashSet<MethodDef> accessorMethods = new HashSet<MethodDef>();
			foreach (var property in type.Properties) {
				accessorMethods.Add(property.GetMethod);
				accessorMethods.Add(property.SetMethod);
				if (property.HasOtherMethods) {
					foreach (var m in property.OtherMethods)
						accessorMethods.Add(m);
				}
			}
			foreach (var ev in type.Events) {
				accessorMethods.Add(ev.AddMethod);
				accessorMethods.Add(ev.RemoveMethod);
				accessorMethods.Add(ev.InvokeMethod);
				if (ev.HasOtherMethods) {
					foreach (var m in ev.OtherMethods)
						accessorMethods.Add(m);
				}
			}
			return accessorMethods;
		}

		public static TypeDef ResolveWithinSameModule(this ITypeDefOrRef type)
		{
			if (type != null && type.Scope == type.Module)
				return type.ResolveTypeDef();
			else
				return null;
		}

		public static FieldDef ResolveFieldWithinSameModule(this MemberRef field)
		{
			if (field != null && field.DeclaringType != null && field.DeclaringType.Scope == field.Module)
				return field.ResolveField();
			else
				return null;
		}

		public static FieldDef ResolveFieldWithinSameModule(this dnlib.DotNet.IField field)
		{
			if (field != null && field.DeclaringType != null && field.DeclaringType.Scope == field.Module)
				return field is FieldDef ? (FieldDef)field : ((MemberRef)field).ResolveField();
			else
				return null;
		}

		public static MethodDef ResolveMethodWithinSameModule(this dnlib.DotNet.IMethod method)
		{
			if (method is MethodSpec)
				method = ((MethodSpec)method).Method;
			if (method != null && method.DeclaringType != null && method.DeclaringType.Scope == method.Module)
				return method is MethodDef ? (MethodDef)method : ((MemberRef)method).ResolveMethod();
			else
				return null;
		}

		public static MethodDef Resolve(this dnlib.DotNet.IMethod method)
		{
			if (method is MethodSpec)
				method = ((MethodSpec)method).Method;
			if (method is MemberRef)
				return ((MemberRef)method).ResolveMethod();
			else
				return (MethodDef)method;
		}

		public static FieldDef Resolve(this dnlib.DotNet.IField field)
		{
			if (field is MemberRef)
				return ((MemberRef)field).ResolveField();
			else
				return (FieldDef)field;
		}

		public static TypeDef Resolve(this dnlib.DotNet.IType type)
		{
			return type == null ? null : type.GetScopeTypeDefOrRef().ResolveTypeDef();
		}

		public static ITypeDefOrRef GetScopeTypeDefOrRef(this dnlib.DotNet.IType type) {
			var t = type.GetScopeType();
			if (t is ITypeDefOrRef tdr)
				return tdr;
			if (t is TypeSig sig)
				return sig.ToTypeDefOrRef();
			return null;
		}

		public static dnlib.DotNet.IType GetScopeType(this dnlib.DotNet.IType type) {
			if (type is TypeDef td)
				return td;
			if (type is TypeRef tr)
				return tr;
			if (!(type is TypeSig sig)) {
				if (!(type is TypeSpec ts))
					return type;
				sig = ts.TypeSig;
			}
			sig = sig.RemovePinnedAndModifiers();
			if (sig is GenericInstSig gis)
				return gis.GenericType?.TypeDefOrRef;
			if (sig is TypeDefOrRefSig tdrs)
				return tdrs.TypeDefOrRef;
			return type;
		}

		public static bool IsCompilerGenerated(this IHasCustomAttribute  provider)
		{
			if (provider != null && provider.HasCustomAttributes) {
				foreach (CustomAttribute a in provider.CustomAttributes) {
					if (a.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute")
						return true;
				}
			}
			return false;
		}

		public static bool IsCompilerGeneratedOrIsInCompilerGeneratedClass(this IMemberDef member)
		{
			if (member == null)
				return false;
			if (member.IsCompilerGenerated())
				return true;
			return IsCompilerGeneratedOrIsInCompilerGeneratedClass(member.DeclaringType);
		}

		public static bool IsAnonymousType(this ITypeDefOrRef type)
		{
			if (type == null)
				return false;
			if (string.IsNullOrEmpty(type.Namespace) && type.HasGeneratedName() && (type.Name.Contains("AnonType") || type.Name.Contains("AnonymousType"))) {
				TypeDef td = type.Resolve();
				return td != null && td.IsCompilerGenerated();
			}
			return false;
		}

		public static bool HasGeneratedName(this IMemberRef member)
		{
			return member.Name.StartsWith("<", StringComparison.Ordinal);
		}

		public static bool ContainsAnonymousType(this TypeSig type)
		{
			return type.ContainsAnonymousType(0);
		}

		static bool ContainsAnonymousType(this TypeSig type, int depth)
		{
			if (depth >= 30)
				return false;
			GenericInstSig git = type as GenericInstSig;
			if (git != null && git.GenericType != null) {
				if (IsAnonymousType(git.GenericType.TypeDefOrRef))
					return true;
				for (int i = 0; i < git.GenericArguments.Count; i++) {
					if (git.GenericArguments[i].ContainsAnonymousType(depth + 1))
						return true;
				}
				return false;
			}
			if (type != null && type.Next != null)
				return type.Next.ContainsAnonymousType(depth + 1);
			return false;
		}

		public static string GetDefaultMemberName(this TypeDef type)
		{
			CustomAttribute attr;
			return type.GetDefaultMemberName(out attr);
		}

		public static string GetDefaultMemberName(this TypeDef type, out CustomAttribute defaultMemberAttribute)
		{
			if (type != null)
				foreach (CustomAttribute ca in type.CustomAttributes.FindAll("System.Reflection.DefaultMemberAttribute"))
					if (ca.Constructor != null && ca.Constructor.FullName == @"System.Void System.Reflection.DefaultMemberAttribute::.ctor(System.String)" &&
						ca.ConstructorArguments.Count == 1 &&
						ca.ConstructorArguments[0].Value is UTF8String) {
						defaultMemberAttribute = ca;
						return (UTF8String)ca.ConstructorArguments[0].Value;
					}
			defaultMemberAttribute = null;
			return null;
		}

		public static bool IsIndexer(this PropertyDef property)
		{
			CustomAttribute attr;
			return property.IsIndexer(out attr);
		}

		public static bool IsIndexer(this PropertyDef property, out CustomAttribute defaultMemberAttribute)
		{
			defaultMemberAttribute = null;
			if (property != null && property.PropertySig.GetParamCount() > 0) {
				var accessor = property.GetMethod ?? property.SetMethod;
				PropertyDef basePropDef = property;
				if (accessor.HasOverrides) {
					// if the property is explicitly implementing an interface, look up the property in the interface:
					MethodDef baseAccessor = accessor.Overrides.First().MethodDeclaration.Resolve();
					if (baseAccessor != null) {
						foreach (PropertyDef baseProp in baseAccessor.DeclaringType.Properties) {
							if (baseProp.GetMethod == baseAccessor || baseProp.SetMethod == baseAccessor) {
								basePropDef = baseProp;
								break;
							}
						}
					} else
						return false;
				}
				CustomAttribute attr;
				var defaultMemberName = basePropDef.DeclaringType.GetDefaultMemberName(out attr);
				if (defaultMemberName == basePropDef.Name) {
					defaultMemberAttribute = attr;
					return true;
				}
			}
			return false;
		}

		public static bool IsUnconditionalBranch(this OpCode opcode)
		{
			if (opcode.OpCodeType == OpCodeType.Prefix)
				return false;
			switch (opcode.FlowControl) {
				case FlowControl.Branch:
				case FlowControl.Throw:
				case FlowControl.Return:
					return true;
				case FlowControl.Next:
				case FlowControl.Call:
				case FlowControl.Cond_Branch:
					return false;
				default:
					throw new NotSupportedException(opcode.FlowControl.ToString());
			}
		}

		public static TypeSystem.FullTypeName GetFullTypeName(this TypeDef typeDef)
		{
			return new TypeSystem.FullTypeName(typeDef.FullName);
		}

		public static bool IsDelegate(this TypeDef type)
		{
			if (type.BaseType != null && type.BaseType.Namespace == "System") {
				if (type.BaseType.Name == "MulticastDelegate")
					return true;
				if (type.BaseType.Name == "Delegate" && type.Name != "MulticastDelegate")
					return true;
			}
			return false;
		}

		public static int GetCodeSize(this CilBody body)
		{
			if (body.Instructions.Count == 0)
				return 0;
			var instr = body.Instructions.Last();
			return instr.GetEndOffset();
		}

		public static IList<Parameter> GetParameters(this IMethod method) {
			if (method is null || method.MethodSig is null)
				return new List<Parameter>();

			if (method is MethodDef md)
				return md.Parameters;

			var list = new List<Parameter>();
			int paramIndex = 0, methodSigIndex = 0;
			if (method.MethodSig.HasThis)
				list.Add(new Parameter(paramIndex++, Parameter.HIDDEN_THIS_METHOD_SIG_INDEX, method.DeclaringType.ToTypeSig()));
			foreach (var type in method.MethodSig.GetParams())
				list.Add(new Parameter(paramIndex++, methodSigIndex++, type));
			return list;
		}

		public static IEnumerable<Parameter> GetParameters(this PropertyDef property)
		{
			if (property == null)
				yield break;
			if (property.GetMethod != null)
			{
				foreach (var param in property.GetMethod.Parameters)
					yield return param;
				yield break;
			}
			if (property.SetMethod != null)
			{
				int last = property.SetMethod.Parameters.Count - 1;
				foreach (var param in property.SetMethod.Parameters)
				{
					if (param.Index != last)
						yield return param;
				}
				yield break;
			}

			int i = 0;
			foreach (TypeSig param in property.PropertySig.GetParameters())
			{
				yield return new Parameter(i,i,param);
				i++;
			}
		}

		public static IList<TypeSig> GetParameters(this MethodBaseSig methodSig)
		{
			if (methodSig == null)
				return new List<TypeSig>();
			if (methodSig.ParamsAfterSentinel != null)
				return methodSig.Params
								.Concat(new TypeSig[] { new SentinelSig() })
								.Concat(methodSig.ParamsAfterSentinel)
								.ToList();
			else
				return methodSig.Params;
		}

		public static int GetParametersSkip(this IList<Parameter> parameters)
		{
			if (parameters == null || parameters.Count == 0)
				return 0;
			if (parameters[0].IsHiddenThisParameter)
				return 1;
			return 0;
		}

		public static int GetNumberOfNormalParameters(this IList<Parameter> parameters)
		{
			if (parameters == null)
				return 0;
			return parameters.Count - GetParametersSkip(parameters);
		}

		public static Instruction GetPrevious(this CilBody body, Instruction instr)
		{
			int index = body.Instructions.IndexOf(instr);
			if (index <= 0)
				return null;
			return body.Instructions[index - 1];
		}

		public static bool IsValueType(ITypeDefOrRef tdr)
		{
			if (tdr == null)
				return false;
			var ts = tdr as TypeSpec;
			if (ts != null)
				return IsValueType(ts.TypeSig);
			return tdr.IsValueType;
		}

		public static bool IsValueType(TypeSig ts) => ts?.IsValueType ?? false;

		public static string GetScopeName(this IScope scope)
		{
			if (scope == null)
				return string.Empty;
			if (scope is IFullName)
				return ((IFullName)scope).Name;
			else
				return scope.ScopeName;	// Shouldn't be reached
		}
	}
}
