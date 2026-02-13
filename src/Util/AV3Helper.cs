#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using Object = UnityEngine.Object;
using System;
using VRC.SDK3.Avatars.Components;

namespace d4rkpl4y3r.AV3ToggleUtil.Util
{
    public static class AV3Helper
    {
        public static VRCAvatarDescriptor FindAvatarDescriptor(GameObject obj)
        {
            if (obj == null)
                return null;
            VRCAvatarDescriptor descriptor;
            while (!obj.TryGetComponent(out descriptor))
            {
                if (obj.transform.parent == null)
                    return null;
                obj = obj.transform.parent.gameObject;
            }
            return descriptor;
        }
    }
}
#endif