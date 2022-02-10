﻿// Copyright (c) 2019 Siegfried Pammer
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
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using dnlib.DotNet;
using System.Text.RegularExpressions;

using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.TypeSystem.Implementation;
using ICSharpCode.Decompiler.Util;

namespace ICSharpCode.Decompiler.IL.Transforms
{
	/// <summary>
	/// Decompiler step for C# 7.0 local functions
	/// </summary>
	public class LocalFunctionDecompiler : IILTransform
	{
		ILTransformContext context;
		ITypeResolveContext resolveContext;

		struct LocalFunctionInfo
		{
			public List<ILInstruction> UseSites;
			public TypeSystem.IMethod Method;
			public ILFunction Definition;
			/// <summary>
			/// Used to store all synthesized call-site arguments grouped by the parameter index.
			/// We use a dictionary instead of a simple array, because -1 is used for the this parameter
			/// and there might be many non-synthesized arguments in between.
			/// </summary>
			public Dictionary<int, List<ILInstruction>> LocalFunctionArguments;
		}

		/// <summary>
		/// The transform works like this:
		///
		/// <para>
		/// local functions can either be used in method calls, i.e., call and callvirt instructions,
		/// or can be used as part of the "delegate construction" pattern, i.e.,
		/// <c>newobj Delegate(&lt;target-expression&gt;, ldftn &lt;method&gt;)</c>.
		/// </para>
		/// As local functions can be declared practically anywhere, we have to take a look at
		/// all use-sites and infer the declaration location from that. Use-sites can be call,
		/// callvirt and ldftn instructions.
		/// After all use-sites are collected we construct the ILAst of the local function
		/// and add it to the parent function.
		/// Then all use-sites of the local-function are transformed to a call to the
		/// <c>LocalFunctionMethod</c> or a ldftn of the <c>LocalFunctionMethod</c>.
		/// In a next step we handle all nested local functions.
		/// After all local functions are transformed, we move all local functions that capture
		/// any variables to their respective declaration scope.
		/// </summary>
		public void Run(ILFunction function, ILTransformContext context)
		{
			if (!context.Settings.LocalFunctions)
				return;
			// Disable the transform if we are decompiling a display-class or local function method:
			// This happens if a local function or display class is selected in the ILSpy tree view.
			if (IsLocalFunctionMethod(function.Method, context) || IsLocalFunctionDisplayClass(
					function.Method.ParentModule.PEFile,
					(TypeDef)function.Method.DeclaringTypeDefinition.MetadataToken,
					context)
				)
			{
				return;
			}

			this.context = context;
			this.resolveContext = new SimpleTypeResolveContext(function.Method);
			var localFunctions = new Dictionary<MethodDef, LocalFunctionInfo>();
			// Find all local functions declared inside this method, including nested local functions or local functions declared in lambdas.
			FindUseSites(function, context, localFunctions);
			ReplaceReferencesToDisplayClassThis(localFunctions.Values);
			DetermineCaptureAndDeclarationScopes(localFunctions.Values);
			PropagateClosureParameterArguments(localFunctions);
			TransformUseSites(localFunctions.Values);
		}

		private void ReplaceReferencesToDisplayClassThis(Dictionary<MethodDef, LocalFunctionInfo>.ValueCollection localFunctions)
		{
			foreach (var info in localFunctions)
			{
				var localFunction = info.Definition;
				if (localFunction.Method.IsStatic)
					continue;
				var thisVar = localFunction.Variables.SingleOrDefault(VariableKindExtensions.IsThis);
				if (thisVar == null)
					continue;
				var compatibleArgument = FindCompatibleArgument(
					info,
					info.UseSites.OfType<CallInstruction>().Select(u => u.Arguments[0]).ToArray(),
					ignoreStructure: true
				);
				if (compatibleArgument == null)
					continue;
				context.Step($"Replace 'this' with {compatibleArgument}", localFunction);
				localFunction.AcceptVisitor(new DelegateConstruction.ReplaceDelegateTargetVisitor(compatibleArgument, thisVar));
				DetermineCaptureAndDeclarationScope(info, -1, compatibleArgument);
			}
		}

		private void DetermineCaptureAndDeclarationScopes(Dictionary<MethodDef, LocalFunctionInfo>.ValueCollection localFunctions)
		{
			foreach (var info in localFunctions)
			{
				context.CancellationToken.ThrowIfCancellationRequested();
				if (info.Definition == null)
				{
					context.Function.Warnings.Add($"Could not decode local function '{info.Method}'");
					continue;
				}

				context.StepStartGroup($"Determine and move to declaration scope of " + info.Definition.Name, info.Definition);
				try
				{
					var localFunction = info.Definition;

					foreach (var useSite in info.UseSites)
					{
						DetermineCaptureAndDeclarationScope(info, useSite);

						if (context.Function.Method.IsConstructor && localFunction.DeclarationScope == null)
						{
							localFunction.DeclarationScope = BlockContainer.FindClosestContainer(useSite);
						}
					}

					if (localFunction.DeclarationScope == null)
					{
						localFunction.DeclarationScope = (BlockContainer)context.Function.Body;
					}

					ILFunction declaringFunction = GetDeclaringFunction(localFunction);
					if (declaringFunction != context.Function)
					{
						context.Step($"Move {localFunction.Name} from {context.Function.Name} to {declaringFunction.Name}", localFunction);
						context.Function.LocalFunctions.Remove(localFunction);
						declaringFunction.LocalFunctions.Add(localFunction);
					}

					if (TryValidateSkipCount(info, out int skipCount) && skipCount != localFunction.ReducedMethod.NumberOfCompilerGeneratedTypeParameters)
					{
						Debug.Assert(false);
						context.Function.Warnings.Add($"Could not decode local function '{info.Method}'");
						if (declaringFunction != context.Function)
						{
							declaringFunction.LocalFunctions.Remove(localFunction);
						}
					}
				}
				finally
				{
					context.StepEndGroup(keepIfEmpty: true);
				}
			}
		}

		private void TransformUseSites(Dictionary<MethodDef, LocalFunctionInfo>.ValueCollection localFunctions)
		{
			foreach (var info in localFunctions)
			{
				context.CancellationToken.ThrowIfCancellationRequested();
				if (info.Definition == null)
					continue;
				context.StepStartGroup($"TransformUseSites of " + info.Definition.Name, info.Definition);
				try
				{
					foreach (var useSite in info.UseSites)
					{
						context.Step($"Transform use-site at IL_{useSite.StartILOffset:X4}", useSite);
						switch (useSite)
						{
							case NewObj newObj:
								TransformToLocalFunctionReference(info.Definition, newObj);
								break;
							case CallInstruction call:
								TransformToLocalFunctionInvocation(info.Definition.ReducedMethod, call);
								break;
							case LdFtn fnptr:
								var specializeMethod = info.Definition.ReducedMethod
									.Specialize(fnptr.Method.Substitution);
								var replacement = new LdFtn(specializeMethod).WithILRange(fnptr);
								fnptr.ReplaceWith(replacement);
								break;
							default:
								throw new NotSupportedException();
						}
					}
				}
				finally
				{
					context.StepEndGroup();
				}
			}
		}

		private void PropagateClosureParameterArguments(Dictionary<MethodDef, LocalFunctionInfo> localFunctions)
		{
			foreach (var localFunction in context.Function.Descendants.OfType<ILFunction>())
			{
				if (localFunction.Kind != ILFunctionKind.LocalFunction)
					continue;
				context.CancellationToken.ThrowIfCancellationRequested();
				var token = (MethodDef)localFunction.Method.MetadataToken;
				var info = localFunctions[token];

				foreach (var useSite in info.UseSites)
				{
					switch (useSite)
					{
						case NewObj newObj:
							AddAsArgument(-1, newObj.Arguments[0]);
							break;
						case CallInstruction call:
							int firstArgumentIndex;
							if (info.Method.IsStatic)
							{
								firstArgumentIndex = 0;
							}
							else
							{
								firstArgumentIndex = 1;
								AddAsArgument(-1, call.Arguments[0]);
							}
							for (int i = call.Arguments.Count - 1; i >= firstArgumentIndex; i--)
							{
								AddAsArgument(i - firstArgumentIndex, call.Arguments[i]);
							}
							break;
						case LdFtn _:
							// &LocalFunction is only possible, if the local function is declared static,
							// this means that the local function can be declared in the top-level scope.
							// Thus, there are no closure parameters that need propagation.
							break;
						default:
							throw new NotSupportedException();
					}
				}

				context.StepStartGroup($"PropagateClosureParameterArguments of " + info.Definition.Name, info.Definition);
				try
				{
					foreach (var (index, arguments) in info.LocalFunctionArguments)
					{
						var targetVariable = info.Definition.Variables.SingleOrDefault(p => p.Kind == VariableKind.Parameter && p.Index == index);
						if (targetVariable == null)
							continue;
						var compatibleArgument = FindCompatibleArgument(info, arguments);
						if (compatibleArgument == null)
							continue;
						context.Step($"Replace '{targetVariable}' with '{compatibleArgument}'", info.Definition);
						info.Definition.AcceptVisitor(new DelegateConstruction.ReplaceDelegateTargetVisitor(compatibleArgument, targetVariable));
					}
				}
				finally
				{
					context.StepEndGroup(keepIfEmpty: true);
				}

				void AddAsArgument(int index, ILInstruction argument)
				{
					switch (argument)
					{
						case LdLoc _:
						case LdLoca _:
						case LdFlda _:
						case LdObj _:
							if (index >= 0 && !IsClosureParameter(info.Method.Parameters[index], resolveContext))
								return;
							break;
						default:
							if (index >= 0 && IsClosureParameter(info.Method.Parameters[index], resolveContext))
								info.Definition.Warnings.Add("Could not transform parameter " + index + ": unsupported argument pattern");
							return;
					}

					if (!info.LocalFunctionArguments.TryGetValue(index, out var arguments))
					{
						arguments = new List<ILInstruction>();
						info.LocalFunctionArguments.Add(index, arguments);
					}
					arguments.Add(argument);
				}
			}
		}

		private ILInstruction FindCompatibleArgument(LocalFunctionInfo info, IList<ILInstruction> arguments, bool ignoreStructure = false)
		{
			foreach (var arg in arguments)
			{
				if (arg is IInstructionWithVariableOperand ld2 && (ignoreStructure || info.Definition.IsDescendantOf(ld2.Variable.Function)))
					return arg;
				var v = ResolveAncestorScopeReference(arg);
				if (v != null)
					return new LdLoc(v);
			}
			return null;
		}

		private ILVariable ResolveAncestorScopeReference(ILInstruction inst)
		{
			if (!inst.MatchLdFld(out var target, out var field))
				return null;
			if (field.Type.Kind != TypeKind.Class)
				return null;
			if (!(TransformDisplayClassUsage.IsPotentialClosure(context, field.Type.GetDefinition()) || context.Function.Method.DeclaringType.Equals(field.Type)))
				return null;
			foreach (var v in context.Function.Descendants.OfType<ILFunction>().SelectMany(f => f.Variables))
			{
				if (!(TransformDisplayClassUsage.IsClosure(context, v, out var varType, out _) && varType.Equals(field.Type)))
					continue;
				return v;
			}
			return null;
		}

		private ILFunction GetDeclaringFunction(ILFunction localFunction)
		{
			if (localFunction.DeclarationScope == null)
				return null;
			ILInstruction inst = localFunction.DeclarationScope;
			while (inst != null)
			{
				if (inst is ILFunction declaringFunction)
					return declaringFunction;
				inst = inst.Parent;
			}
			return null;
		}

		bool TryValidateSkipCount(LocalFunctionInfo info, out int skipCount)
		{
			skipCount = 0;
			var localFunction = info.Definition;
			if (localFunction.Method.TypeParameters.Count == 0)
				return true;
			var parentMethod = ((ILFunction)localFunction.Parent).Method;
			var method = localFunction.Method;
			skipCount = parentMethod.DeclaringType.TypeParameterCount - method.DeclaringType.TypeParameterCount;

			if (skipCount > 0)
				return false;
			skipCount += parentMethod.TypeParameters.Count;
			if (skipCount < 0 || skipCount > method.TypeArguments.Count)
				return false;

			if (skipCount > 0)
			{
#if DEBUG
				foreach (var useSite in info.UseSites)
				{
					var callerMethod = useSite.Ancestors.OfType<ILFunction>().First().Method;
					callerMethod = callerMethod.ReducedFrom ?? callerMethod;
					TypeSystem.IMethod m;
					switch (useSite)
					{
						case NewObj newObj:
							m = ((LdFtn)newObj.Arguments[1]).Method;
							break;
						case CallInstruction call:
							m = call.Method;
							break;
						case LdFtn fnptr:
							m = fnptr.Method;
							break;
						default:
							throw new NotSupportedException();
					}
					var totalSkipCount = skipCount + m.DeclaringType.TypeParameterCount;
					var methodSkippedArgs = m.DeclaringType.TypeArguments.Concat(m.TypeArguments).Take(totalSkipCount);
					Debug.Assert(methodSkippedArgs.SequenceEqual(callerMethod.DeclaringType.TypeArguments.Concat(callerMethod.TypeArguments).Take(totalSkipCount)));
					Debug.Assert(methodSkippedArgs.All(p => p.Kind == TypeKind.TypeParameter));
					Debug.Assert(methodSkippedArgs.Select(p => p.Name).SequenceEqual(m.DeclaringType.TypeParameters.Concat(m.TypeParameters).Take(totalSkipCount).Select(p => p.Name)));
				}
#endif
			}
			return true;
		}

		void FindUseSites(ILFunction function, ILTransformContext context, Dictionary<MethodDef, LocalFunctionInfo> localFunctions)
		{
			foreach (var inst in function.Body.Descendants)
			{
				context.CancellationToken.ThrowIfCancellationRequested();
				if (inst is CallInstruction call && !call.Method.IsLocalFunction && IsLocalFunctionMethod(call.Method, context))
				{
					HandleUseSite(call.Method, call);
				}
				else if (inst is LdFtn ldftn && !ldftn.Method.IsLocalFunction && IsLocalFunctionMethod(ldftn.Method, context))
				{
					if (ldftn.Parent is NewObj newObj && DelegateConstruction.MatchDelegateConstruction(newObj, out _, out _, out _))
						HandleUseSite(ldftn.Method, newObj);
					else
						HandleUseSite(ldftn.Method, ldftn);
				}
			}

			void HandleUseSite(TypeSystem.IMethod targetMethod, ILInstruction inst)
			{
				if (!localFunctions.TryGetValue((MethodDef)targetMethod.MetadataToken, out var info))
				{
					context.StepStartGroup($"Read local function '{targetMethod.Name}'", inst);
					info = new LocalFunctionInfo() {
						UseSites = new List<ILInstruction>() { inst },
						LocalFunctionArguments = new Dictionary<int, List<ILInstruction>>(),
						Method = (TypeSystem.IMethod)targetMethod.MemberDefinition,
					};
					var rootFunction = context.Function;
					int skipCount = GetSkipCount(rootFunction, targetMethod);
					info.Definition = ReadLocalFunctionDefinition(rootFunction, targetMethod, skipCount);
					localFunctions.Add((MethodDef)targetMethod.MetadataToken, info);
					if (info.Definition != null)
					{
						FindUseSites(info.Definition, context, localFunctions);
					}
					context.StepEndGroup();
				}
				else
				{
					info.UseSites.Add(inst);
				}
			}
		}

		ILFunction ReadLocalFunctionDefinition(ILFunction rootFunction, TypeSystem.IMethod targetMethod, int skipCount)
		{
			var methodDefinition = (MethodDef)targetMethod.MetadataToken;
			var genericContext = GenericContextFromTypeArguments(targetMethod, skipCount);
			if (genericContext == null)
				return null;
			ILFunction function;
			bool hasBody = methodDefinition.HasBody;
			if (!hasBody)
			{
				function = new ILFunction(targetMethod, 0,
					new TypeSystem.GenericContext(genericContext?.ClassTypeParameters, genericContext?.MethodTypeParameters),
					new Nop(), ILFunctionKind.LocalFunction);
			}
			else
			{
				var ilReader = context.CreateILReader();
				function = ilReader.ReadIL(methodDefinition,
					genericContext.GetValueOrDefault(), ILFunctionKind.LocalFunction,
					context.CancellationToken);
			}
			// Embed the local function into the parent function's ILAst, so that "Show steps" can show
			// how the local function body is being transformed.
			rootFunction.LocalFunctions.Add(function);
			if (hasBody)
			{
				function.DeclarationScope = (BlockContainer)rootFunction.Body;
				function.CheckInvariant(ILPhase.Normal);
				var nestedContext = new ILTransformContext(context, function);
				function.RunTransforms(CSharpDecompiler.GetILTransforms().TakeWhile(t => !(t is LocalFunctionDecompiler)), nestedContext);
				function.DeclarationScope = null;
			}
			function.ReducedMethod = ReduceToLocalFunction(function.Method, skipCount);
			return function;
		}

		int GetSkipCount(ILFunction rootFunction, TypeSystem.IMethod targetMethod)
		{
			targetMethod = (TypeSystem.IMethod)targetMethod.MemberDefinition;
			var skipCount = rootFunction.Method.DeclaringType.TypeParameters.Count + rootFunction.Method.TypeParameters.Count - targetMethod.DeclaringType.TypeParameters.Count;
			if (skipCount < 0)
			{
				skipCount = 0;
			}
			if (targetMethod.TypeParameters.Count > 0)
			{
				var lastParams = targetMethod.Parameters.Where(p => IsClosureParameter(p, this.resolveContext)).SelectMany(p => UnwrapByRef(p.Type).TypeArguments)
											 .Select(pt => (int?)targetMethod.TypeParameters.IndexOf(pt)).DefaultIfEmpty().Max();
				if (lastParams != null && lastParams.GetValueOrDefault() + 1 > skipCount)
					skipCount = lastParams.GetValueOrDefault() + 1;
			}
			return skipCount;
		}

		static TypeSystem.GenericContext? GenericContextFromTypeArguments(TypeSystem.IMethod targetMethod, int skipCount)
		{
			if (skipCount < 0 || skipCount > targetMethod.TypeParameters.Count)
			{
				Debug.Assert(false);
				return null;
			}
			int total = targetMethod.DeclaringType.TypeParameters.Count + skipCount;
			if (total == 0)
				return default(TypeSystem.GenericContext);

			var classTypeParameters = new List<ITypeParameter>(targetMethod.DeclaringType.TypeParameters);
			var methodTypeParameters = new List<ITypeParameter>(targetMethod.TypeParameters);
			var skippedTypeArguments = targetMethod.DeclaringType.TypeArguments.Concat(targetMethod.TypeArguments).Take(total);
			int idx = 0;
			foreach (var skippedTA in skippedTypeArguments)
			{
				int curIdx;
				List<ITypeParameter> curParameters;
				IReadOnlyList<TypeSystem.IType> curArgs;
				if (idx < classTypeParameters.Count)
				{
					curIdx = idx;
					curParameters = classTypeParameters;
					curArgs = targetMethod.DeclaringType.TypeArguments;
				}
				else
				{
					curIdx = idx - classTypeParameters.Count;
					curParameters = methodTypeParameters;
					curArgs = targetMethod.TypeArguments;
				}
				if (curArgs[curIdx].Kind != TypeKind.TypeParameter)
					break;
				curParameters[curIdx] = (ITypeParameter)skippedTA;
				idx++;
			}
			if (idx != total)
			{
				Debug.Assert(false);
				return null;
			}

			return new TypeSystem.GenericContext(classTypeParameters, methodTypeParameters);
		}

		static T FindCommonAncestorInstruction<T>(ILInstruction a, ILInstruction b)
			where T : ILInstruction
		{
			var ancestorsOfB = new HashSet<T>(b.Ancestors.OfType<T>());
			return a.Ancestors.OfType<T>().FirstOrDefault(ancestorsOfB.Contains);
		}

		internal static bool IsClosureParameter(IParameter parameter, ITypeResolveContext context)
		{
			if (!parameter.IsRef)
				return false;
			var type = ((ByReferenceType)parameter.Type).ElementType.GetDefinition();
			return type != null
				&& type.Kind == TypeKind.Struct
				&& TransformDisplayClassUsage.IsPotentialClosure(context.CurrentTypeDefinition, type);
		}

		static TypeSystem.IType UnwrapByRef(TypeSystem.IType type)
		{
			if (type is ByReferenceType byRef)
			{
				type = byRef.ElementType;
			}
			return type;
		}

		internal static ILInstruction GetStatement(ILInstruction inst)
		{
			while (inst.Parent != null)
			{
				if (inst.Parent is Block b && b.Kind == BlockKind.ControlFlow)
					return inst;
				inst = inst.Parent;
			}
			return inst;
		}

		LocalFunctionMethod ReduceToLocalFunction(TypeSystem.IMethod method, int typeParametersToRemove)
		{
			int parametersToRemove = 0;
			for (int i = method.Parameters.Count - 1; i >= 0; i--)
			{
				if (!IsClosureParameter(method.Parameters[i], resolveContext))
					break;
				parametersToRemove++;
			}
			return new LocalFunctionMethod(method, method.Name, CanBeStaticLocalFunction(), parametersToRemove, typeParametersToRemove);

			bool CanBeStaticLocalFunction()
			{
				if (!context.Settings.StaticLocalFunctions)
					return false;
				// Cannot be static because there are closure parameters that will be removed
				if (parametersToRemove > 0)
					return false;
				// no closure parameters, but static:
				// we can safely assume, this local function can be declared static
				if (method.IsStatic)
					return true;
				// the local function is used in conjunction with a lambda, which means,
				// it is defined inside the display-class type
				var declaringType = method.DeclaringTypeDefinition;
				if (!declaringType.IsCompilerGenerated())
					return false;
				// if there are no instance fields, we can make it a static local function
				return !declaringType.GetFields(f => !f.IsStatic).Any();
			}
		}

		static void TransformToLocalFunctionReference(ILFunction function, CallInstruction useSite)
		{
			ILInstruction target = useSite.Arguments[0];
			target.ReplaceWith(new LdNull().WithILRange(target));
			if (target is IInstructionWithVariableOperand withVar && withVar.Variable.Kind == VariableKind.Local)
			{
				withVar.Variable.Kind = VariableKind.DisplayClassLocal;
			}
			var fnptr = (IInstructionWithMethodOperand)useSite.Arguments[1];
			var specializeMethod = function.ReducedMethod.Specialize(fnptr.Method.Substitution);
			var replacement = new LdFtn(specializeMethod).WithILRange((ILInstruction)fnptr);
			useSite.Arguments[1].ReplaceWith(replacement);
		}

		void TransformToLocalFunctionInvocation(LocalFunctionMethod reducedMethod, CallInstruction useSite)
		{
			var specializeMethod = reducedMethod.Specialize(useSite.Method.Substitution);
			bool wasInstanceCall = !useSite.Method.IsStatic;
			var replacement = new Call(specializeMethod);
			int firstArgumentIndex = wasInstanceCall ? 1 : 0;
			int argumentCount = useSite.Arguments.Count;
			int reducedArgumentCount = argumentCount - (reducedMethod.NumberOfCompilerGeneratedParameters + firstArgumentIndex);
			replacement.Arguments.AddRange(useSite.Arguments.Skip(firstArgumentIndex).Take(reducedArgumentCount));
			// copy flags
			replacement.ConstrainedTo = useSite.ConstrainedTo;
			replacement.ILStackWasEmpty = useSite.ILStackWasEmpty;
			replacement.IsTail = useSite.IsTail;
			// copy IL ranges
			replacement.AddILRange(useSite);
			if (wasInstanceCall)
			{
				replacement.AddILRange(useSite.Arguments[0]);
				if (useSite.Arguments[0].MatchLdLocRef(out var variable) && variable.Kind == VariableKind.NamedArgument)
				{
					// remove the store instruction of the simple load, if it is a named argument.
					var storeInst = (ILInstruction)variable.StoreInstructions[0];
					((Block)storeInst.Parent).Instructions.RemoveAt(storeInst.ChildIndex);
				}
			}
			for (int i = 0; i < reducedMethod.NumberOfCompilerGeneratedParameters; i++)
			{
				replacement.AddILRange(useSite.Arguments[argumentCount - i - 1]);
			}
			useSite.ReplaceWith(replacement);
		}

		void DetermineCaptureAndDeclarationScope(LocalFunctionInfo info, ILInstruction useSite)
		{
			switch (useSite)
			{
				case CallInstruction call:
					int firstArgumentIndex = info.Definition.Method.IsStatic ? 0 : 1;
					for (int i = call.Arguments.Count - 1; i >= firstArgumentIndex; i--)
					{
						if (!DetermineCaptureAndDeclarationScope(info, i - firstArgumentIndex, call.Arguments[i]))
							break;
					}
					if (firstArgumentIndex > 0)
					{
						DetermineCaptureAndDeclarationScope(info, -1, call.Arguments[0]);
					}
					break;
				case LdFtn _:
					// &LocalFunction is only possible, if the local function is declared static,
					// this means that the local function can be declared in the top-level scope.
					// leave info.DeclarationScope null/unassigned.
					break;
				default:
					throw new NotSupportedException();
			}
		}

		bool DetermineCaptureAndDeclarationScope(LocalFunctionInfo info, int parameterIndex, ILInstruction arg)
		{
			ILFunction function = info.Definition;
			ILVariable closureVar;
			if (parameterIndex >= 0)
			{
				if (!(parameterIndex < function.Method.Parameters.Count
					&& IsClosureParameter(function.Method.Parameters[parameterIndex], resolveContext)))
				{
					return false;
				}
			}
			if (!(arg.MatchLdLoc(out closureVar) || arg.MatchLdLoca(out closureVar)))
			{
				closureVar = ResolveAncestorScopeReference(arg);
				if (closureVar == null)
					return false;
			}
			if (closureVar.Kind == VariableKind.NamedArgument)
				return false;
			var initializer = GetClosureInitializer(closureVar);
			if (initializer == null)
				return false;
			// determine the capture scope of closureVar and the declaration scope of the function
			var additionalScope = BlockContainer.FindClosestContainer(initializer);
			if (closureVar.CaptureScope == null)
				closureVar.CaptureScope = additionalScope;
			else
			{
				BlockContainer combinedScope = FindCommonAncestorInstruction<BlockContainer>(closureVar.CaptureScope, additionalScope);
				Debug.Assert(combinedScope != null);
				closureVar.CaptureScope = combinedScope;
			}
			if (closureVar.Kind == VariableKind.Local)
			{
				closureVar.Kind = VariableKind.DisplayClassLocal;
			}
			if (function.DeclarationScope == null)
				function.DeclarationScope = closureVar.CaptureScope;
			else if (!IsInNestedLocalFunction(function.DeclarationScope, closureVar.CaptureScope.Ancestors.OfType<ILFunction>().First()))
				function.DeclarationScope = FindCommonAncestorInstruction<BlockContainer>(function.DeclarationScope, closureVar.CaptureScope);
			return true;

			ILInstruction GetClosureInitializer(ILVariable variable)
			{
				var type = UnwrapByRef(variable.Type).GetDefinition();
				if (type == null)
					return null;
				if (variable.Kind == VariableKind.Parameter)
					return null;
				if (type.Kind == TypeKind.Struct)
					return GetStatement(variable.AddressInstructions.OrderBy(i => i.StartILOffset).First());
				else
					return (StLoc)variable.StoreInstructions[0];
			}
		}

		bool IsInNestedLocalFunction(BlockContainer declarationScope, ILFunction function)
		{
			return TreeTraversal.PreOrder(function, f => f.LocalFunctions).Any(f => declarationScope.IsDescendantOf(f.Body));
		}

		internal static bool IsLocalFunctionReference(NewObj inst, ILTransformContext context)
		{
			if (inst == null || inst.Arguments.Count != 2 || inst.Method.DeclaringType.Kind != TypeKind.Delegate)
				return false;
			var opCode = inst.Arguments[1].OpCode;

			return opCode == OpCode.LdFtn
				&& IsLocalFunctionMethod(((IInstructionWithMethodOperand)inst.Arguments[1]).Method, context);
		}

		public static bool IsLocalFunctionMethod(TypeSystem.IMethod method, ILTransformContext context)
		{
			if (method.MetadataToken is not MethodDef def)
				return false;
			return IsLocalFunctionMethod(method.ParentModule.PEFile, (MethodDef)def, context);
		}

		public static bool IsLocalFunctionMethod(PEFile module, MethodDef method, ILTransformContext context = null)
		{
			if (context != null && context.PEFile != module)
				return false;

			var declaringType = method.DeclaringType;
			if ((method.Attributes & MethodAttributes.Assembly) == 0 || !(method.IsCompilerGenerated() || declaringType.IsCompilerGenerated()))
				return false;

			if (!ParseLocalFunctionName(method.Name, out _, out _))
				return false;

			return true;
		}

		public static bool LocalFunctionNeedsAccessibilityChange(PEFile module, MethodDef method)
		{
			if (!IsLocalFunctionMethod(module, method))
				return false;

			List<TypeDef> refStructTypes = new List<TypeDef>();
			VisitFindRefStruct(method.ReturnType, refStructTypes);
			foreach (TypeSig sig in method.MethodSig.Params) {
				VisitFindRefStruct(sig, refStructTypes);
			}

			foreach (var td in refStructTypes)
			{
				if (td.IsCompilerGenerated() && td.IsValueType)
					return true;
			}

			return false;
		}

		public static bool IsLocalFunctionDisplayClass(PEFile module, TypeDef type, ILTransformContext context = null)
		{
			if (context != null && context.PEFile != module)
				return false;

			if ((type.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.NestedPrivate)
				return false;
			if (!type.HasGeneratedName())
				return false;

			var declaringType = type.DeclaringType;

			foreach (var method in declaringType.Methods)
			{
				if (!IsLocalFunctionMethod(module, method, context))
					continue;

				bool canBe = false;
				foreach (TypeSig sig in method.MethodSig.Params)
				{
					canBe |= VisitFindType(sig, type);
				}
				if (canBe)
					return true;
			}

			return false;
		}

		private static bool VisitFindType(TypeSig sig, TypeDef type)
		{
			switch (sig) {
				case SZArraySig _:
				case CorLibTypeSig _:
				case GenericVar _:
				case FnPtrSig _:
				case GenericSig _:
					return false;
				case PtrSig ptrSig:
					return VisitFindType(ptrSig.Next, type);
				case PinnedSig pinnedSig:
					return VisitFindType(pinnedSig.Next, type);
				case ModifierSig modifierSig:
					return VisitFindType(modifierSig.Next, type);
				case GenericInstSig genericInstSig:
					return VisitFindType(genericInstSig.GenericType, type);
				case ByRefSig byRefSig:
					return VisitFindType(byRefSig.Next, type);
				case ArraySig arraySig:
					return VisitFindType(arraySig.Next, type);
				case ClassOrValueTypeSig classOrValueTypeSig:
					if (classOrValueTypeSig.IsTypeSpec)
						return VisitFindType(classOrValueTypeSig.TypeSpec.TypeSig, type);
					else if (classOrValueTypeSig.IsTypeRef)
						return false;
					else
						return classOrValueTypeSig.TypeDef == type;
				default:
					throw new ArgumentOutOfRangeException();
			}
		}

		private static void VisitFindRefStruct(TypeSig sig, List<TypeDef> refTypes)
		{
			switch (sig) {
				case SZArraySig _:
				case CorLibTypeSig _:
				case GenericVar _:
				case FnPtrSig _:
				case PtrSig _:
				case GenericSig _:
				case PinnedSig _:
				case ModifierSig _:
				case GenericInstSig _:
				case ArraySig _:
					return;
				case ByRefSig byRefSig:
					VisitFindRefStruct(byRefSig.Next, refTypes);
					return;
				case ClassOrValueTypeSig classOrValueTypeSig when classOrValueTypeSig.IsTypeDef:
					refTypes.Add(classOrValueTypeSig.TypeDef);
					return;
				default:
					return;
			}
		}

		/// <summary>
		/// Newer Roslyn versions use the format "&lt;callerName&gt;g__functionName|x_y"
		/// Older versions use "&lt;callerName&gt;g__functionNamex_y"
		/// </summary>
		static readonly Regex functionNameRegex = new Regex(@"^<(.*)>g__([^\|]*)\|{0,1}\d+(_\d+)?$", RegexOptions.Compiled);

		internal static bool ParseLocalFunctionName(string name, out string callerName, out string functionName)
		{
			callerName = null;
			functionName = null;
			if (string.IsNullOrWhiteSpace(name))
				return false;
			var match = functionNameRegex.Match(name);
			callerName = match.Groups[1].Value;
			functionName = match.Groups[2].Value;
			return match.Success;
		}
	}
}
