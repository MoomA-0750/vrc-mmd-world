using MmdWorld.Vmd;
using UnityEditor;
using UnityEngine;

namespace MmdWorld.EditorTools
{
    /// <summary>
    /// お手本用の人形。VMD の変換に使うリグと同じ寸法の骨に、カプセルを付けただけのもの。
    /// 特定のアバターに頼らずに、変換したモーションを Unity の中で確かめるのにも使う。
    /// </summary>
    public static class MannequinBuilder
    {
        public static GameObject BuildPrefab(string prefabPath, string avatarPath, Material material)
        {
            using (var rig = new MmdHumanoidRig(new MmdSkeleton(), "Mannequin", HideFlags.None))
            {
                var avatar = rig.Avatar;
                avatar.name = "MannequinAvatar";
                AssetDatabase.DeleteAsset(avatarPath);
                AssetDatabase.CreateAsset(avatar, avatarPath);

                var root = rig.Root;
                foreach (var t in root.GetComponentsInChildren<Transform>())
                {
                    if (t == root.transform) continue;
                    AddVisual(t, material);
                }

                var animator = root.AddComponent<Animator>();
                animator.avatar = avatar;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                return prefab;
            }
        }

        static void AddVisual(Transform bone, Material material)
        {
            string n = bone.name;
            float radius = n.Contains("Thumb") || n.Contains("Index") || n.Contains("Middle") || n.Contains("Ring") || n.Contains("Little") ? 0.007f
                : n.Contains("Arm") || n.Contains("Hand") || n.Contains("Shoulder") ? 0.03f
                : n.Contains("Leg") || n.Contains("Foot") || n.Contains("Toes") ? 0.045f
                : n == "Neck" ? 0.04f
                : 0.08f;

            if (n == "Head")
            {
                var head = Primitive(PrimitiveType.Sphere, bone, material);
                head.localPosition = new Vector3(0f, 0.1f, 0f);
                head.localScale = new Vector3(0.18f, 0.22f, 0.2f);
                // 前がわかるように鼻をつける
                var nose = Primitive(PrimitiveType.Cube, bone, material);
                nose.localPosition = new Vector3(0f, 0.1f, 0.1f);
                nose.localScale = new Vector3(0.03f, 0.03f, 0.05f);
                return;
            }

            if (bone.childCount == 0)
            {
                var tip = Primitive(PrimitiveType.Sphere, bone, material);
                tip.localPosition = Vector3.zero;
                tip.localScale = Vector3.one * radius * 2f;
                return;
            }

            foreach (Transform child in bone)
            {
                // 手から指へは付けない（手のひらは Hand から中指の付け根まで1本で表す）
                if (n.EndsWith("Hand") && !child.name.Contains("Middle")) continue;
                Vector3 to = child.localPosition;
                if (to.magnitude < 1e-4f) continue;
                var limb = Primitive(PrimitiveType.Capsule, bone, material);
                limb.localPosition = to * 0.5f;
                limb.localRotation = Quaternion.FromToRotation(Vector3.up, to);
                float r = n.EndsWith("Hand") ? 0.035f : radius;
                limb.localScale = new Vector3(r * 2f, to.magnitude * 0.5f + r, r * 2f);
            }
        }

        static Transform Primitive(PrimitiveType type, Transform parent, Material material)
        {
            var go = GameObject.CreatePrimitive(type);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.name = "Visual";
            go.GetComponent<Renderer>().sharedMaterial = material;
            go.transform.SetParent(parent, false);
            return go.transform;
        }
    }
}
