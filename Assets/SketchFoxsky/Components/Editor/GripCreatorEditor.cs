#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(GripCreator))]
[CanEditMultipleObjects]
public class GripCreatorEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        GUILayout.Space(10);

        if (GUILayout.Button("Create / Replace Grip On Selected"))
        {
            foreach (Object obj in targets)
            {
                GripCreator gripCreator = (GripCreator)obj;

                Undo.RegisterFullObjectHierarchyUndo(
                    gripCreator.gameObject,
                    "Create Grip"
                );

                gripCreator.CreateGrip();

                EditorUtility.SetDirty(gripCreator.gameObject);
            }
        }
    }
}
#endif
