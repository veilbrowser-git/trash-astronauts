using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TrashAstronauts.EditorTools
{
    /// <summary>
    /// Convenience: when the editor opens this project on an empty untitled scene, load the game scene
    /// so pressing Play just works. Does nothing in batch mode or if you already have a scene open.
    /// </summary>
    static class OpenSceneOnLoad
    {
        [InitializeOnLoadMethod]
        static void Hook()
        {
            if (Application.isBatchMode) return;
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                var active = EditorSceneManager.GetActiveScene();
                if (!string.IsNullOrEmpty(active.path) || active.isDirty) return;
                if (File.Exists(Builder.ScenePath)) EditorSceneManager.OpenScene(Builder.ScenePath, OpenSceneMode.Single);
            };
        }
    }
}
