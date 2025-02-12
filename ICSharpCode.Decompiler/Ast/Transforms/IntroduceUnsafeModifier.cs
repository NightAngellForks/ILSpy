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

using System.Linq;
using dnlib.DotNet;
using ICSharpCode.NRefactory.CSharp;

namespace ICSharpCode.Decompiler.Ast.Transforms {
	public sealed class IntroduceUnsafeModifier : DepthFirstAstVisitor<object, bool>, IAstTransformPoolObject
	{
		public static readonly object PointerArithmeticAnnotation = new PointerArithmetic();

		sealed class PointerArithmetic {}

		public void Run(AstNode compilationUnit)
		{
			compilationUnit.AcceptVisitor(this, null);
		}

		public void Reset(DecompilerContext context)
		{
		}

		protected override bool VisitChildren(AstNode node, object data)
		{
			bool result = false;
			AstNode next;
			for (AstNode child = node.FirstChild; child != null; child = next) {
				// Store next to allow the loop to continue
				// if the visitor removes/replaces child.
				next = child.NextSibling;
				result |= child.AcceptVisitor(this, data);
			}
			if (result && node is EntityDeclaration && !(node is Accessor)) {
				var ed = (EntityDeclaration)node;
				ed.Modifiers |= Modifiers.Unsafe;

				// Make sure the comments are still shown before the method and its modifiers
				var comments = ed.GetChildrenByRole(Roles.Comment).Reverse().ToArray();
				foreach (var c in comments) {
					c.Remove();
					ed.InsertChildAfter(null, c, Roles.Comment);
				}
				return false;
			}
			return result;
		}

		public override bool VisitPointerReferenceExpression(PointerReferenceExpression pointerReferenceExpression, object data)
		{
			base.VisitPointerReferenceExpression(pointerReferenceExpression, data);
			return true;
		}

		public override bool VisitSizeOfExpression(SizeOfExpression sizeOfExpression, object data)
		{
			var result = base.VisitSizeOfExpression(sizeOfExpression, data);

			// C# sizeof(MyStruct) requires unsafe{} but C# sizeof(int) does not.
			var type = sizeOfExpression.Type.Annotation<ITypeDefOrRef>();
			if (type is null)
				return result;
			return !type.IsPrimitive;
		}

		public override bool VisitFunctionPointerType(FunctionPointerAstType functionPointerType, object data) => true;

		public override bool VisitComposedType(ComposedType composedType, object data)
		{
			if (composedType.PointerRank > 0)
				return true;
			else
				return base.VisitComposedType(composedType, data);
		}

		public override bool VisitUnaryOperatorExpression(UnaryOperatorExpression unaryOperatorExpression, object data)
		{
			bool result = base.VisitUnaryOperatorExpression(unaryOperatorExpression, data);
			if (unaryOperatorExpression.Operator == UnaryOperatorType.Dereference) {
				BinaryOperatorExpression bop = unaryOperatorExpression.Expression as BinaryOperatorExpression;
				if (bop != null && bop.Operator == BinaryOperatorType.Add && bop.Annotation<PointerArithmetic>() != null) {
					// transform "*(ptr + int)" to "ptr[int]"
					IndexerExpression indexer = new IndexerExpression();
					indexer.Target = bop.Left.Detach();
					indexer.Arguments.Add(bop.Right.Detach());
					indexer.CopyAnnotationsFrom(unaryOperatorExpression);
					indexer.CopyAnnotationsFrom(bop);
					indexer.AddAnnotation(unaryOperatorExpression.GetAllRecursiveILSpans());
					unaryOperatorExpression.ReplaceWith(indexer);
				}
				return true;
			} else if (unaryOperatorExpression.Operator == UnaryOperatorType.AddressOf) {
				return true;
			} else {
				return result;
			}
		}

		public override bool VisitMemberReferenceExpression(MemberReferenceExpression memberReferenceExpression, object data)
		{
			bool result = base.VisitMemberReferenceExpression(memberReferenceExpression, data);
			UnaryOperatorExpression uoe = memberReferenceExpression.Target as UnaryOperatorExpression;
			if (uoe != null && uoe.Operator == UnaryOperatorType.Dereference) {
				PointerReferenceExpression pre = new PointerReferenceExpression();
				pre.Target = uoe.Expression.Detach();
				pre.MemberNameToken = (Identifier)memberReferenceExpression.MemberNameToken.Clone();
				memberReferenceExpression.TypeArguments.MoveTo(pre.TypeArguments);
				pre.CopyAnnotationsFrom(uoe);
				pre.CopyAnnotationsFrom(memberReferenceExpression);
				pre.AddAnnotation(memberReferenceExpression.GetAllRecursiveILSpans());
				memberReferenceExpression.ReplaceWith(pre);
			}
			return result;
		}

		public override bool VisitStackAllocExpression(StackAllocExpression stackAllocExpression, object data)
		{
			base.VisitStackAllocExpression(stackAllocExpression, data);
			return true;
		}

		public override bool VisitFixedVariableInitializer(FixedVariableInitializer fixedVariableInitializer, object data)
		{
			base.VisitFixedVariableInitializer(fixedVariableInitializer, data);
			return true;
		}
	}
}
