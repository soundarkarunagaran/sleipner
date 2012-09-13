﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace DR.Sleipner.CacheProxyCore
{
    public class ReflectionEmitProxyGenerator : IProxyGenerator
    {
        private static readonly AssemblyBuilder AssemblyBuilder;
        private static readonly ModuleBuilder ModuleBuilder;

        static ReflectionEmitProxyGenerator()
        {
            var currentDomain = AppDomain.CurrentDomain;
            var dynamicAssemblyName = new AssemblyName
                              {
                                  Name = "SleipnerCacheProxies",
                              };

            AssemblyBuilder = currentDomain.DefineDynamicAssembly(dynamicAssemblyName, AssemblyBuilderAccess.RunAndSave);
            ModuleBuilder = AssemblyBuilder.DefineDynamicModule("SleipnerCacheProxies", "SleipnerCacheProxies.dll");
        }

        public Type CreateProxy<T>() where T : class
        {
            var proxyType = typeof(T);
            var baseType = typeof(CacheProxyBase<T>);
            var typeBuilder = ModuleBuilder.DefineType(proxyType.FullName + "__Proxy", TypeAttributes.Class | TypeAttributes.Public, baseType, new[] { typeof(T) });

            var cTor = baseType.GetConstructor(new[] { typeof(T) }); //Get the constructor that takes the generic type as it's only parameter.

            //Create the constructor
            var cTorBuilder = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(T) });
            var cTorBody = cTorBuilder.GetILGenerator();
            cTorBody.Emit(OpCodes.Ldarg_0);         //Load this on stack
            cTorBody.Emit(OpCodes.Ldarg_1);         //Load the first parameter of the constructor on stack
            cTorBody.Emit(OpCodes.Call, cTor);      //Call base constructor
            cTorBody.Emit(OpCodes.Ret);             //Return

            var getCachedItemMethod = baseType.GetMethod("GetCachedItem");
            var storeItemMethod = baseType.GetMethod("StoreItem");
            var realInstanceField = baseType.GetField("RealInstance");

            foreach (var method in proxyType.GetMethods()) //We guarantee internally that this is the methods of an interface. The compiler will gurantee that these are all the methods that needs proxying.
            {
                var parameterTypes = method.GetParameters().Select(a => a.ParameterType).ToArray();
                var proxyMethod = typeBuilder.DefineMethod(method.Name, MethodAttributes.Public | MethodAttributes.Virtual, CallingConventions.HasThis, method.ReturnType, parameterTypes);
                var methodBody = proxyMethod.GetILGenerator();

                if (method.ReturnType == typeof(void))
                {
                    methodBody.Emit(OpCodes.Ldarg_0);                           //Load this on the stack
                    methodBody.Emit(OpCodes.Ldfld, realInstanceField);          //Load the real instance on the stack
                    for (var i = 0; i < parameterTypes.Length; i++)             //Load all parameters on the stack
                    {
                        methodBody.Emit(OpCodes.Ldarg, i + 1);                  //Method parameter on stack
                    }

                    methodBody.Emit(OpCodes.Callvirt, method);                  //Call the method in question on the instance
                    methodBody.Emit(OpCodes.Ret);                               //Load this on the stack

                    continue;
                }

                var methodParameterArray = methodBody.DeclareLocal(typeof(object[]));
                var cachedItem = methodBody.DeclareLocal(method.ReturnType);
                var noCacheLabel = methodBody.DefineLabel();

                methodBody.Emit(OpCodes.Ldc_I4, parameterTypes.Length);     //Push array size on stack
                methodBody.Emit(OpCodes.Newarr, typeof(object));            //Create array
                methodBody.Emit(OpCodes.Stloc, methodParameterArray);       //Store array in arr

                for (var i = 0; i < parameterTypes.Length; i++)             //Load all parameters on the stack
                {
                    var parameterType = parameterTypes[i];
                    methodBody.Emit(OpCodes.Ldloc, methodParameterArray);   //Push array reference
                    methodBody.Emit(OpCodes.Ldc_I4, i);                     //Push array index index
                    methodBody.Emit(OpCodes.Ldarg, i + 1);                  //Push array index value

                    if (parameterType.IsValueType)
                    {
                        methodBody.Emit(OpCodes.Box, parameterType);        //Value types need to be boxed into their type
                    }

                    methodBody.Emit(OpCodes.Stelem_Ref);                    //Store element in array
                }

                methodBody.Emit(OpCodes.Ldarg_0);                           //Load this on the stack
                methodBody.Emit(OpCodes.Ldstr, method.Name);                //Load the first parameter value on the stack (name of the method being called)
                methodBody.Emit(OpCodes.Ldloc, methodParameterArray);       //Load the array on the stack
                methodBody.Emit(OpCodes.Callvirt, getCachedItemMethod);     //Call the interceptMethod
                methodBody.Emit(OpCodes.Castclass, method.ReturnType);      //Case returned item (since intercept returns object
                methodBody.Emit(OpCodes.Stloc, cachedItem);                 //Store the result of the method call in a local variable. This also pops it from the stack.

                methodBody.Emit(OpCodes.Ldloc, cachedItem);
                methodBody.Emit(OpCodes.Brfalse, noCacheLabel);             //Check if null is on the stack. If it is go to noCacheLabel

                methodBody.Emit(OpCodes.Ldloc, cachedItem);                 //Load cached item on the stack
                methodBody.Emit(OpCodes.Ret);                               //Return to caller

                methodBody.MarkLabel(noCacheLabel);                         //noCacheLabelMark. The method needs to call the instance method and get a response
                methodBody.Emit(OpCodes.Ldarg_0);                           //Load this on the stack
                methodBody.Emit(OpCodes.Ldfld, realInstanceField);          //Load the real instance on the stack

                for (var i = 0; i < parameterTypes.Length; i++)             //Load all parameters on the stack
                {
                    methodBody.Emit(OpCodes.Ldarg, i + 1);                  //Method parameter on stack
                }

                methodBody.Emit(OpCodes.Callvirt, method);                  //Call the method in question on the instance
                methodBody.Emit(OpCodes.Stloc, cachedItem);                 //And throw the result in a variable

                methodBody.Emit(OpCodes.Ldarg_0);                           //Load this on the stack
                methodBody.Emit(OpCodes.Ldstr, method.Name);                //Load the first parameter value on the stack (name of the method being called)
                methodBody.Emit(OpCodes.Ldloc, methodParameterArray);       //Load the array of parameters on the stack
                methodBody.Emit(OpCodes.Ldloc, cachedItem);                 //Load item to cache on stack
                methodBody.Emit(OpCodes.Callvirt, storeItemMethod);         //Call the storeItem (this is a void method so we don't need to pop the result)

                methodBody.Emit(OpCodes.Ldloc, cachedItem);                 //Load the cached item on the stack
                methodBody.Emit(OpCodes.Ret);                               //Return to caller

                typeBuilder.DefineMethodOverride(proxyMethod, method);
            }

            var createdType = typeBuilder.CreateType();

            #if DEBUG
            AssemblyBuilder.Save("SleipnerCacheProxies.dll"); //If we're running in debugging mode we're going to save this assembly on disc since we might need to ILSpy it.
            #endif
            
            return createdType;
        }
    }
}
