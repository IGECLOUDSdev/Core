// Copyright 2004-2021 Castle Project - http://www.castleproject.org/
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace Castle.DynamicProxy.Generators
{
	using System;
	using System.Collections.Generic;
	using System.Reflection;

	using Castle.DynamicProxy.Contributors;
	using Castle.DynamicProxy.Generators.Emitters;
	using Castle.DynamicProxy.Generators.Emitters.SimpleAST;
	using Castle.DynamicProxy.Internal;
	using Castle.DynamicProxy.Tokens;

	internal abstract class InvocationTypeGenerator : IGenerator<AbstractTypeEmitter>
	{
		protected readonly MetaMethod method;
		protected readonly Type targetType;
		private readonly MethodInfo callback;
		private readonly bool canChangeTarget;
		private readonly IInvocationCreationContributor contributor;

		protected InvocationTypeGenerator(Type targetType, MetaMethod method, MethodInfo callback, bool canChangeTarget,
		                                  IInvocationCreationContributor contributor)
		{
			this.targetType = targetType;
			this.method = method;
			this.callback = callback;
			this.canChangeTarget = canChangeTarget;
			this.contributor = contributor;
		}

		/// <summary>
		///   Generates the constructor for the class that extends
		///   <see cref = "AbstractInvocation" />
		/// </summary>
		protected abstract ArgumentReference[] GetBaseCtorArguments(Type targetFieldType,
		                                                            out ConstructorInfo baseConstructor);

		protected abstract Type GetBaseType();

		protected abstract FieldReference GetTargetReference();

		public AbstractTypeEmitter Generate(ClassEmitter @class, INamingScope namingScope)
		{
			var methodInfo = method.Method;

			var interfaces = Type.EmptyTypes;

			if (canChangeTarget)
			{
				interfaces = new[] { typeof(IChangeProxyTarget) };
			}
			var invocation = GetEmitter(@class, interfaces, namingScope, methodInfo);

			// invocation only needs to mirror the generic parameters of the MethodInfo
			// targetType cannot be a generic type definition (YET!)
			invocation.CopyGenericParametersFromMethod(methodInfo);

			CreateConstructor(invocation);

			var targetField = GetTargetReference();
			if (canChangeTarget)
			{
				ImplementChangeProxyTargetInterface(@class, invocation, targetField);
			}

			ImplementInvokeMethodOnTarget(invocation, methodInfo.GetParameters(), targetField, callback);

#if FEATURE_SERIALIZATION
			invocation.DefineCustomAttribute<SerializableAttribute>();
#endif

			return invocation;
		}

		protected virtual MethodInvocationExpression GetCallbackMethodInvocation(AbstractTypeEmitter invocation,
		                                                                         IExpression[] args, MethodInfo callbackMethod,
		                                                                         Reference targetField,
		                                                                         MethodEmitter invokeMethodOnTarget)
		{
			if (contributor != null)
			{
				return contributor.GetCallbackMethodInvocation(invocation, args, targetField, invokeMethodOnTarget);
			}
			var methodOnTargetInvocationExpression = new MethodInvocationExpression(
				new AsTypeReference(targetField, callbackMethod.DeclaringType),
				callbackMethod,
				args) { VirtualCall = true };
			return methodOnTargetInvocationExpression;
		}

		protected virtual void ImplementInvokeMethodOnTarget(AbstractTypeEmitter invocation, ParameterInfo[] parameters,
		                                                     MethodEmitter invokeMethodOnTarget,
		                                                     Reference targetField)
		{
			var callbackMethod = GetCallbackMethod(invocation);
			if (callbackMethod == null)
			{
				EmitCallThrowOnNoTarget(invokeMethodOnTarget);
				return;
			}

			var args = new IExpression[parameters.Length];

			// Idea: instead of grab parameters one by one
			// we should grab an array
			var byRefArguments = new Dictionary<int, LocalReference>();

			for (var i = 0; i < parameters.Length; i++)
			{
				var param = parameters[i];

				var paramType = invocation.GetClosedParameterType(param.ParameterType);
				if (paramType.IsByRef)
				{
					var localReference = invokeMethodOnTarget.CodeBuilder.DeclareLocal(paramType.GetElementType());
					invokeMethodOnTarget.CodeBuilder
						.AddStatement(
							new AssignStatement(localReference,
							                    new ConvertExpression(paramType.GetElementType(),
							                                          new MethodInvocationExpression(SelfReference.Self,
							                                                                         InvocationMethods.GetArgumentValue,
							                                                                         new LiteralIntExpression(i)))));
					var byRefReference = new ByRefReference(localReference);
					args[i] = byRefReference;
					byRefArguments[i] = localReference;
				}
				else
				{
					args[i] =
						new ConvertExpression(paramType,
						                      new MethodInvocationExpression(SelfReference.Self,
						                                                     InvocationMethods.GetArgumentValue,
						                                                     new LiteralIntExpression(i)));
				}
			}

			if (byRefArguments.Count > 0)
			{
				invokeMethodOnTarget.CodeBuilder.AddStatement(new TryStatement());
			}

			var methodOnTargetInvocationExpression = GetCallbackMethodInvocation(invocation, args, callbackMethod, targetField, invokeMethodOnTarget);

			LocalReference returnValue = null;
			if (callbackMethod.ReturnType != typeof(void))
			{
				var returnType = invocation.GetClosedParameterType(callbackMethod.ReturnType);
				returnValue = invokeMethodOnTarget.CodeBuilder.DeclareLocal(returnType);
				invokeMethodOnTarget.CodeBuilder.AddStatement(new AssignStatement(returnValue, methodOnTargetInvocationExpression));
			}
			else
			{
				invokeMethodOnTarget.CodeBuilder.AddStatement(methodOnTargetInvocationExpression);
			}

			AssignBackByRefArguments(invokeMethodOnTarget, byRefArguments);

			if (callbackMethod.ReturnType != typeof(void))
			{
				var setRetVal =
					new MethodInvocationExpression(SelfReference.Self,
					                               InvocationMethods.SetReturnValue,
					                               new ConvertExpression(typeof(object), returnValue.Type, returnValue));

				invokeMethodOnTarget.CodeBuilder.AddStatement(setRetVal);
			}

			invokeMethodOnTarget.CodeBuilder.AddStatement(new ReturnStatement());
		}

		private void AssignBackByRefArguments(MethodEmitter invokeMethodOnTarget, Dictionary<int, LocalReference> byRefArguments)
		{
			if (byRefArguments.Count == 0)
			{
				return;
			}

			invokeMethodOnTarget.CodeBuilder.AddStatement(new FinallyStatement());
			foreach (var byRefArgument in byRefArguments)
			{
				var index = byRefArgument.Key;
				var localReference = byRefArgument.Value;
				invokeMethodOnTarget.CodeBuilder.AddStatement(
					new MethodInvocationExpression(
						SelfReference.Self,
						InvocationMethods.SetArgumentValue,
						new LiteralIntExpression(index),
						new ConvertExpression(
							typeof(object),
							localReference.Type,
							localReference)));
			}
			invokeMethodOnTarget.CodeBuilder.AddStatement(new EndExceptionBlockStatement());
		}

		private void CreateConstructor(AbstractTypeEmitter invocation)
		{
			ConstructorInfo baseConstructor;
			var baseCtorArguments = GetBaseCtorArguments(targetType, out baseConstructor);

			var constructor = CreateConstructor(invocation, baseCtorArguments);
			constructor.CodeBuilder.AddStatement(new ConstructorInvocationStatement(baseConstructor, baseCtorArguments));
			constructor.CodeBuilder.AddStatement(new ReturnStatement());
		}

		private ConstructorEmitter CreateConstructor(AbstractTypeEmitter invocation, ArgumentReference[] baseCtorArguments)
		{
			if (contributor == null)
			{
				return invocation.CreateConstructor(baseCtorArguments);
			}
			return contributor.CreateConstructor(baseCtorArguments, invocation);
		}

		private void EmitCallThrowOnNoTarget(MethodEmitter invokeMethodOnTarget)
		{
			var throwOnNoTarget = new MethodInvocationExpression(InvocationMethods.ThrowOnNoTarget);

			invokeMethodOnTarget.CodeBuilder.AddStatement(throwOnNoTarget);
			invokeMethodOnTarget.CodeBuilder.AddStatement(new ReturnStatement());
		}

		private MethodInfo GetCallbackMethod(AbstractTypeEmitter invocation)
		{
			if (contributor != null)
			{
				return contributor.GetCallbackMethod();
			}
			var callbackMethod = callback;
			if (callbackMethod == null)
			{
				return null;
			}

			if (!callbackMethod.IsGenericMethod)
			{
				return callbackMethod;
			}

			return callbackMethod.MakeGenericMethod(invocation.GetGenericArgumentsFor(callbackMethod));
		}

		private AbstractTypeEmitter GetEmitter(ClassEmitter @class, Type[] interfaces, INamingScope namingScope,
		                                       MethodInfo methodInfo)
		{
			var suggestedName = string.Format("Castle.Proxies.Invocations.{0}_{1}", methodInfo.DeclaringType.Name,
			                                  methodInfo.Name);
			var uniqueName = namingScope.ParentScope.GetUniqueName(suggestedName);
			return new ClassEmitter(@class.ModuleScope, uniqueName, GetBaseType(), interfaces, ClassEmitter.DefaultAttributes, forceUnsigned: @class.InStrongNamedModule == false);
		}

		private void ImplementInvokeMethodOnTarget(AbstractTypeEmitter invocation, ParameterInfo[] parameters,
		                                           FieldReference targetField, MethodInfo callbackMethod)
		{
			var invokeMethodOnTarget = invocation.CreateMethod("InvokeMethodOnTarget", typeof(void));
			ImplementInvokeMethodOnTarget(invocation, parameters, invokeMethodOnTarget, targetField);
		}

		private void ImplementChangeInvocationTarget(AbstractTypeEmitter invocation, FieldReference targetField)
		{
			var changeInvocationTarget = invocation.CreateMethod("ChangeInvocationTarget", typeof(void), new[] { typeof(object) });
			changeInvocationTarget.CodeBuilder.AddStatement(
				new AssignStatement(targetField,
				                    new ConvertExpression(targetType, changeInvocationTarget.Arguments[0])));
			changeInvocationTarget.CodeBuilder.AddStatement(new ReturnStatement());
		}

		private void ImplementChangeProxyTarget(AbstractTypeEmitter invocation, ClassEmitter @class)
		{
			var changeProxyTarget = invocation.CreateMethod("ChangeProxyTarget", typeof(void), new[] { typeof(object) });

			var proxyObject = new FieldReference(InvocationMethods.ProxyObject);
			var localProxy = changeProxyTarget.CodeBuilder.DeclareLocal(typeof(IProxyTargetAccessor));
			changeProxyTarget.CodeBuilder.AddStatement(
				new AssignStatement(localProxy,
					new ConvertExpression(localProxy.Type, proxyObject)));

			var dynSetProxy = typeof(IProxyTargetAccessor).GetMethod(nameof(IProxyTargetAccessor.DynProxySetTarget));

			changeProxyTarget.CodeBuilder.AddStatement(
				new MethodInvocationExpression(localProxy, dynSetProxy, changeProxyTarget.Arguments[0])
				{
					VirtualCall = true
				});

			changeProxyTarget.CodeBuilder.AddStatement(new ReturnStatement());
		}

		private void ImplementChangeProxyTargetInterface(ClassEmitter @class, AbstractTypeEmitter invocation,
		                                                 FieldReference targetField)
		{
			ImplementChangeInvocationTarget(invocation, targetField);

			ImplementChangeProxyTarget(invocation, @class);
		}
	}
}