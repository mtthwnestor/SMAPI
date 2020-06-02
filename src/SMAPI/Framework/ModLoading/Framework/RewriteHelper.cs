using System;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace StardewModdingAPI.Framework.ModLoading.Framework
{
    /// <summary>Provides helper methods for field rewriters.</summary>
    internal static class RewriteHelper
    {
        /*********
        ** Fields
        *********/
        /// <summary>The comparer which heuristically compares type definitions.</summary>
        private static readonly TypeReferenceComparer TypeDefinitionComparer = new TypeReferenceComparer();


        /*********
        ** Public methods
        *********/
        /// <summary>Get the field reference from an instruction if it matches.</summary>
        /// <param name="instruction">The IL instruction.</param>
        public static FieldReference AsFieldReference(Instruction instruction)
        {
            return instruction.OpCode == OpCodes.Ldfld || instruction.OpCode == OpCodes.Ldsfld || instruction.OpCode == OpCodes.Stfld || instruction.OpCode == OpCodes.Stsfld
                ? (FieldReference)instruction.Operand
                : null;
        }

        /// <summary>Get whether the field is a reference to the expected type and field.</summary>
        /// <param name="instruction">The IL instruction.</param>
        /// <param name="fullTypeName">The full type name containing the expected field.</param>
        /// <param name="fieldName">The name of the expected field.</param>
        public static bool IsFieldReferenceTo(Instruction instruction, string fullTypeName, string fieldName)
        {
            FieldReference fieldRef = RewriteHelper.AsFieldReference(instruction);
            return RewriteHelper.IsFieldReferenceTo(fieldRef, fullTypeName, fieldName);
        }

        /// <summary>Get whether the field is a reference to the expected type and field.</summary>
        /// <param name="fieldRef">The field reference to check.</param>
        /// <param name="fullTypeName">The full type name containing the expected field.</param>
        /// <param name="fieldName">The name of the expected field.</param>
        public static bool IsFieldReferenceTo(FieldReference fieldRef, string fullTypeName, string fieldName)
        {
            return
                fieldRef != null
                && fieldRef.DeclaringType.FullName == fullTypeName
                && fieldRef.Name == fieldName;
        }

        /// <summary>Get the method reference from an instruction if it matches.</summary>
        /// <param name="instruction">The IL instruction.</param>
        public static MethodReference AsMethodReference(Instruction instruction)
        {
            return instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt || instruction.OpCode == OpCodes.Newobj
                ? (MethodReference)instruction.Operand
                : null;
        }

        /// <summary>Get whether a type matches a type reference.</summary>
        /// <param name="type">The defined type.</param>
        /// <param name="reference">The type reference.</param>
        public static bool IsSameType(Type type, TypeReference reference)
        {
            // 
            // duplicated by IsSameType(TypeReference, TypeReference) below
            //

            // same namespace & name
            if (type.Namespace != reference.Namespace || type.Name != reference.Name)
                return false;

            // same generic parameters
            if (type.IsGenericType)
            {
                if (!reference.IsGenericInstance)
                    return false;

                Type[] defGenerics = type.GetGenericArguments();
                TypeReference[] refGenerics = ((GenericInstanceType)reference).GenericArguments.ToArray();
                if (defGenerics.Length != refGenerics.Length)
                    return false;
                for (int i = 0; i < defGenerics.Length; i++)
                {
                    if (!RewriteHelper.IsSameType(defGenerics[i], refGenerics[i]))
                        return false;
                }
            }

            return true;
        }

        /// <summary>Get whether a type matches a type reference.</summary>
        /// <param name="type">The defined type.</param>
        /// <param name="reference">The type reference.</param>
        public static bool IsSameType(TypeReference type, TypeReference reference)
        {
            // 
            // duplicated by IsSameType(Type, TypeReference) above
            //

            // same namespace & name
            if (type.Namespace != reference.Namespace || type.Name != reference.Name)
                return false;

            // same generic parameters
            if (type.IsGenericInstance)
            {
                if (!reference.IsGenericInstance)
                    return false;

                TypeReference[] defGenerics = ((GenericInstanceType)type).GenericArguments.ToArray();
                TypeReference[] refGenerics = ((GenericInstanceType)reference).GenericArguments.ToArray();
                if (defGenerics.Length != refGenerics.Length)
                    return false;
                for (int i = 0; i < defGenerics.Length; i++)
                {
                    if (!RewriteHelper.IsSameType(defGenerics[i], refGenerics[i]))
                        return false;
                }
            }

            return true;
        }

        /// <summary>Determine whether two type IDs look like the same type, accounting for placeholder values such as !0.</summary>
        /// <param name="typeA">The type ID to compare.</param>
        /// <param name="typeB">The other type ID to compare.</param>
        /// <returns>true if the type IDs look like the same type, false if not.</returns>
        public static bool LooksLikeSameType(TypeReference typeA, TypeReference typeB)
        {
            return RewriteHelper.TypeDefinitionComparer.Equals(typeA, typeB);
        }

        /// <summary>Get whether a method definition matches the signature expected by a method reference.</summary>
        /// <param name="definition">The method definition.</param>
        /// <param name="reference">The method reference.</param>
        public static bool HasMatchingSignature(MethodBase definition, MethodReference reference)
        {
            // 
            // duplicated by HasMatchingSignature(MethodDefinition, MethodReference) below
            //

            // same name
            if (definition.Name != reference.Name)
                return false;

            // same arguments
            ParameterInfo[] definitionParameters = definition.GetParameters();
            ParameterDefinition[] referenceParameters = reference.Parameters.ToArray();
            if (referenceParameters.Length != definitionParameters.Length)
                return false;
            for (int i = 0; i < referenceParameters.Length; i++)
            {
                if (!RewriteHelper.IsSameType(definitionParameters[i].ParameterType, referenceParameters[i].ParameterType))
                    return false;
            }
            return true;
        }

        /// <summary>Get whether a method definition matches the signature expected by a method reference.</summary>
        /// <param name="definition">The method definition.</param>
        /// <param name="reference">The method reference.</param>
        public static bool HasMatchingSignature(MethodDefinition definition, MethodReference reference)
        {
            // 
            // duplicated by HasMatchingSignature(MethodBase, MethodReference) above
            //

            // same name
            if (definition.Name != reference.Name)
                return false;

            // same arguments
            ParameterDefinition[] definitionParameters = definition.Parameters.ToArray();
            ParameterDefinition[] referenceParameters = reference.Parameters.ToArray();
            if (referenceParameters.Length != definitionParameters.Length)
                return false;
            for (int i = 0; i < referenceParameters.Length; i++)
            {
                if (!RewriteHelper.IsSameType(definitionParameters[i].ParameterType, referenceParameters[i].ParameterType))
                    return false;
            }
            return true;
        }

        /// <summary>Get whether a type has a method whose signature matches the one expected by a method reference.</summary>
        /// <param name="type">The type to check.</param>
        /// <param name="reference">The method reference.</param>
        public static bool HasMatchingSignature(Type type, MethodReference reference)
        {
            if (reference.Name == ".ctor")
            {
                return type
                    .GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly | BindingFlags.Public)
                    .Any(method => RewriteHelper.HasMatchingSignature(method, reference));
            }

            return type
                .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly | BindingFlags.Public)
                .Any(method => RewriteHelper.HasMatchingSignature(method, reference));
        }
    }
}
