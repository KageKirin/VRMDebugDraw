using System;
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
    }
}
