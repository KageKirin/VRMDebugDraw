using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Collections;
using UnityEngine;

namespace VRMDebugDraw
{
    /// <summary>
    /// extension methods for Unity classes
    /// </summary>
    public static class UnityExtensions
    {
        public static T GetOrAddComponent<T>(this GameObject gameObject)
            where T : Component
        {
            T component = gameObject.GetComponent<T>();
            if (component == null)
            {
                component = gameObject.AddComponent<T>();
            }

            return component;
        }

        public static GameObject GetOrAddChildObject(this GameObject gameObject, string childName)
        {
            var transforms = gameObject.GetComponentsInChildren<Transform>(true);
            int childIndex = Array.FindIndex(transforms, t => t.name == childName);

            if (childIndex >= 0)
            {
                return transforms[childIndex].gameObject;
            }

            GameObject child = new(childName);
            child.transform.parent = gameObject.transform;
            return child;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NativeArray<T0> ToNativeArray<T0>(this T0[] list, Allocator allocator)
            where T0 : struct
        {
            return new NativeArray<T0>(list, allocator);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NativeArray<T0> ToNativeArray<T0>(this IEnumerable<T0> list, Allocator allocator)
            where T0 : struct
        {
            return list.ToArray().ToNativeArray(allocator);
        }
    }
}
