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
using System.Threading;
using dnlib.DotNet;
using ICSharpCode.Decompiler;

namespace ICSharpCode.ILSpy.TreeNodes.Analyzer
{
	internal sealed class AnalyzedTypeUsedByTreeNode : AnalyzerSearchTreeNode
	{
		private readonly TypeDef analyzedType;

		public AnalyzedTypeUsedByTreeNode(TypeDef analyzedType)
		{
			if (analyzedType == null)
				throw new ArgumentNullException(nameof(analyzedType));

			this.analyzedType = analyzedType;
		}

		public override object Text
		{
			get { return "Used By"; }
		}

		protected override IEnumerable<AnalyzerTreeNode> FetchChildren(CancellationToken ct)
		{
			var analyzer = new ScopedWhereUsedAnalyzer<AnalyzerTreeNode>(analyzedType, FindTypeUsage);
			return analyzer.PerformAnalysis(ct)
				.Cast<AnalyzerEntityTreeNode>()
				.Where(n => n.Member.DeclaringType != analyzedType)
				.Distinct(new AnalyzerEntityTreeNodeComparer())
				.OrderBy(n => n.Text);
		}

		private IEnumerable<AnalyzerEntityTreeNode> FindTypeUsage(TypeDef type)
		{
			if (type == analyzedType)
				yield break;

			if (IsUsedInTypeDefinition(type))
				yield return new AnalyzedTypeTreeNode(type) { Language = Language };

			foreach (var field in type.Fields.Where(IsUsedInFieldReference))
				yield return new AnalyzedFieldTreeNode(field) { Language = Language };

			foreach (var method in type.Methods.Where(IsUsedInMethodDefinition))
				yield return HandleSpecialMethodNode(method);
		}

		private AnalyzerEntityTreeNode HandleSpecialMethodNode(MethodDef method)
		{
			var property = method.DeclaringType.Properties.FirstOrDefault(p => p.GetMethod == method || p.SetMethod == method);
			if (property != null)
				return new AnalyzedPropertyTreeNode(property) { Language = Language };

			return new AnalyzedMethodTreeNode(method) { Language = Language };
		}

		private bool IsUsedInTypeReferences(IEnumerable<ITypeDefOrRef> types)
		{
			return types.Any(IsUsedInTypeReference);
		}

		private bool IsUsedInTypeReference(ITypeDefOrRef type)
		{
			if (type == null)
				return false;

			return TypeMatches(type.DeclaringType)
				|| TypeMatches(type);
		}

		private bool IsUsedInTypeDefinition(TypeDef type)
		{
			return IsUsedInTypeReference(type)
				   || TypeMatches(type.BaseType)
				   || IsUsedInTypeReferences(type.Interfaces.Select(i => i.Interface));
		}

		private bool IsUsedInFieldReference(IField field)
		{
			if (field == null)
				return false;

			return TypeMatches(field.DeclaringType)
				|| TypeMatches(field.FieldSig.Type.ToTypeDefOrRef());
		}

		private bool IsUsedInMethodReference(IMethod method)
		{
			if (method == null)
				return false;

			return TypeMatches(method.DeclaringType)
				   || TypeMatches(method.MethodSig.RetType.ToTypeDefOrRef())
				   || IsUsedInMethodParameters(method.MethodSig.Params);
		}

		private bool IsUsedInMethodDefinition(MethodDef method)
		{
			return IsUsedInMethodReference(method)
				   || IsUsedInMethodBody(method);
		}

		private bool IsUsedInMethodBody(MethodDef method)
		{
			if (method.Body == null)
				return false;

			bool found = false;

			foreach (var instruction in method.Body.Instructions) {
				ITypeDefOrRef tr = instruction.Operand as ITypeDefOrRef;
				if (IsUsedInTypeReference(tr)) {
					found = true;
					break;
				}
				IField fr = instruction.Operand as IField;
				if (IsUsedInFieldReference(fr)) {
					found = true;
					break;
				}
				IMethod mr = instruction.Operand as IMethod;
				if (IsUsedInMethodReference(mr)) {
					found = true;
					break;
				}
			}

			method.Body = null; // discard body to reduce memory pressure & higher GC gen collections

			return found;
		}

		private bool IsUsedInMethodParameters(IEnumerable<TypeSig> parameters)
		{
			return parameters.Any(IsUsedInMethodParameter);
		}

		private bool IsUsedInMethodParameter(TypeSig parameter)
		{
			return TypeMatches(parameter.ToTypeDefOrRef());
		}

		private bool TypeMatches(ITypeDefOrRef tref)
		{
			if (tref != null && tref.Name == analyzedType.Name) {
				var tdef = tref.Resolve();
				if (tdef != null) {
					return (tdef == analyzedType);
				}
			}
			return false;
		}

		public static bool CanShow(TypeDef type)
		{
			return type != null;
		}
	}

	internal class AnalyzerEntityTreeNodeComparer : IEqualityComparer<AnalyzerEntityTreeNode>
	{
		public bool Equals(AnalyzerEntityTreeNode x, AnalyzerEntityTreeNode y)
		{
			return x.Member == y.Member;
		}

		public int GetHashCode(AnalyzerEntityTreeNode node)
		{
			return node.Member.GetHashCode();
		}
	}

}
