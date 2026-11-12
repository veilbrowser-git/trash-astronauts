using System.Linq;
using TrashAstronauts.Net.Online;
using UnityEditor;
using UnityEngine;

namespace TrashAstronauts.EditorTools
{
    public static partial class Builder
    {
        /// <summary>
        /// Links the project to its Unity Cloud project (Relay and Lobby for online play) by writing the ids into
        /// ProjectSettings.asset, where Application.cloudProjectId reads them in the editor and in every build.
        /// The editor's Services window would do the same after a sign-in; batch builds have no sign-in, so the
        /// Builder writes them on every BuildAll. Writing an id enables no Unity service by itself: UnityConnect
        /// (engine analytics, crash and performance reporting) stays off, and Unity Services are only initialised
        /// when the player chooses an online game (see OnlineServices).
        /// </summary>
        static void ConfigureCloudProject()
        {
            Object settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset").FirstOrDefault(o => o is PlayerSettings);
            if (settings == null)
            {
                Debug.LogWarning("[Builder] PlayerSettings not found; the cloud project is not linked.");
                return;
            }
            var so = new SerializedObject(settings);
            SetString(so, "cloudProjectId", OnlineConfig.CloudProjectId);
            SetString(so, "organizationId", OnlineConfig.OrganizationId);
            SetString(so, "projectName", OnlineConfig.ProjectName);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(settings);
            if (Application.cloudProjectId != OnlineConfig.CloudProjectId)
                Debug.LogWarning("[Builder] Application.cloudProjectId is '" + Application.cloudProjectId + "' after linking; expected " + OnlineConfig.CloudProjectId);
        }

        static void SetString(SerializedObject so, string property, string value)
        {
            SerializedProperty p = so.FindProperty(property);
            if (p == null)
            {
                Debug.LogWarning("[Builder] PlayerSettings has no '" + property + "' field.");
                return;
            }
            p.stringValue = value;
        }
    }
}
