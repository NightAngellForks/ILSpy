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
	internal class AnalyzedTypeExtensionMethodsTreeNode : AnalyzerSearchTreeNode
	{
		private readonly TypeDef analyzedType;

		public AnalyzedTypeExtensionMethodsTreeNode(TypeDef analyzedType)
		{
			if (analyzedType == null)
				throw new ArgumentNullException(nameof(analyzedType));

			this.analyzedType = analyzedType;
		}

		public override object Text
		{
			get { return "Extension Methods"; }
		}

		protected override IEnumerable<AnalyzerTreeNode> FetchChildren(CancellationToken ct)
		{
			var analyzer = new ScopedWhereUsedAnalyzer<AnalyzerTreeNode>(analyzedType, FindReferencesInType);
			return analyzer.PerformAnalysis(ct).OrderBy(n => n.Text);
		}

		private IEnumerable<AnalyzerTreeNode> FindReferencesInType(TypeDef type)
		{
			if (!HasExtensionAttribute(type))
				yield break;
			foreach (MethodDef method in type.Methods) {
				if (method.IsStatic && HasExtensionAttribute(method)) {
					if (method.Parameters.Count > 0 && method.Parameters[0].Type.Resolve() == analyzedType) {
						var node = new AnalyzedMethodTreeNode(method);
						node.Language = this.Language;
						yield return node;
					}
				}
			}
		}

		bool HasExtensionAttribute(IHasCustomAttribute p)
		{
			if (p.HasCustomAttributes) {
				foreach (CustomAttribute ca in p.CustomAttributes) {
					ITypeDefOrRef t = ca.AttributeType;
					if (t.Name == "ExtensionAttribute" && t.Namespace == "System.Runtime.CompilerServices")
						return true;
				}
			}
			return false;
		}


		public static bool CanShow(TypeDef type)
		{
			// show on all types except static classes
			return !(type.IsAbstract && type.IsSealed);
		}
	}
}
