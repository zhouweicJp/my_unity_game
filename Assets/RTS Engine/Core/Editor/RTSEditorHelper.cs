﻿using System.Collections.Generic;
using System.Linq;
using System;

using UnityEngine;
using UnityEditor;

using RTSEngine.Entities;

using System.Reflection;

namespace RTSEngine.EditorOnly
{
    public enum RTSEditorProperties
    {
        advancedBuildingPlacement,
    }

    public static partial class RTSEditorPropertiesHandler
    {
        private static Dictionary<RTSEditorProperties, bool> properties = new Dictionary<RTSEditorProperties, bool>();

        public static void EnableProperty(RTSEditorProperties property)
        {
            if (properties.ContainsKey(property))
            {
                properties[property] = true;
                return;
            }

            properties.Add(property, true);
        }

        public static bool IsPropertyEnabled(RTSEditorProperties property)
            => properties.IsValid() && properties.TryGetValue(property, out bool val) && val;
    }

    [InitializeOnLoad]
    public static class RTSEditorHelper
    {
        #region General
        static int count = 0;

        public static event Action OnRTSPrefabsAndAssetsReload;

        static RTSEditorHelper()
        {
            UnityEditor.SceneManagement.PrefabStage.prefabStageOpened += OnPrefabOpen;
            UnityEditor.SceneManagement.PrefabStage.prefabStageClosing += OnPrefabClose;

            EditorApplication.update += Update;
        }

        public static GameObject CurrentPrefab { private set; get; }

        private static void OnPrefabClose(UnityEditor.SceneManagement.PrefabStage obj)
        {
            CurrentPrefab = null;
        }

        private static void OnPrefabOpen(UnityEditor.SceneManagement.PrefabStage obj)
        {
            CurrentPrefab = obj.prefabContentsRoot;
        }

        static void Update()
        {
            if (PlayerPrefs.GetInt("RTSEngineShowPopupWindow", 0) == 0)
            {
                PlayerPrefs.SetInt("RTSEngineShowPopupWindow", 1);
                RTSEnginePopupWindow.Init();
            }

            if (++count > 500)
            {
                count = 0;
                SlowTick();
            }
        }

        static void SlowTick()
        {
            ReloadRTSPrefabsAndAssetFiles();
            EditorApplication.update -= Update;
        }

        [MenuItem("RTS Engine/Refresh Asset Files _F5", false, 1001)]
        private static void ReloadRTSPrefabsAndAssetFiles()
        {
            RefreshAssetFiles(requireTest: false, testInstance: null);
            FetchEntityPrefabs();

            Debug.Log("[RTSEditorHelper] Cached RTS Engine related scriptable objects and entity prefabs.");

            var handler = OnRTSPrefabsAndAssetsReload;
            handler?.Invoke();
        }

        public static string CurrentProjectFolderPath
        {
            get
            {
                Type projectWindowUtilType = typeof(ProjectWindowUtil);
                MethodInfo getActiveFolderPath = projectWindowUtilType.GetMethod("GetActiveFolderPath", BindingFlags.Static | BindingFlags.NonPublic);
                object obj = getActiveFolderPath.Invoke(null, new object[0]);
                return obj.ToString();
            }
        }

        internal static void OpenDocumentation()
        {
            Application.OpenURL("https://docs.gamedevspice.com/rts_engine/manual/index.html");
        }
        #endregion

        #region Asset Files (extending RTSEngineScriptableObject)
        private static IReadOnlyDictionary<Type, IEnumerable<RTSEngineScriptableObject>> AssetFiles = null;

        public static void RefreshAssetFiles (bool requireTest, RTSEngineScriptableObject testInstance) 
        {
            //Type nextType = testInstance ? testInstance.GetType() : typeof(RTSEngineScriptableObject);
            Type nextType = typeof(RTSEngineScriptableObject);
            if(!requireTest 
                || AssetFiles == null 
                || !AssetFiles.TryGetValue(nextType, out IEnumerable<RTSEngineScriptableObject> cache)
                || !cache.Contains(testInstance))
            {
                var newAssetFiles = new Dictionary<Type, IEnumerable<RTSEngineScriptableObject>>();

                if(TryGetAllAssetFiles(out List<RTSEngineScriptableObject> assets, filter: $"t:{nextType.ToString()}"))
                {
                    var groupedAssets = assets
                        .GroupBy(asset => asset.GetType());

                    foreach(var group in groupedAssets)
                    {
                        if (newAssetFiles.ContainsKey(group.Key))
                            newAssetFiles[group.Key] = group;
                        else
                            newAssetFiles.Add(group.Key, group);
                    }
                }

                AssetFiles = newAssetFiles;
            }
        }

        private static bool TryGetAllAssetFiles <T>(out List<T> assets, string filter = "DefaultAsset l:noLabel t:noType") where T : ScriptableObject
        {
            assets = new List<T> { };
            string[] guids = AssetDatabase.FindAssets(filter);

            if (guids.Length > 0)
            {
                foreach (string guid in guids)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    assets.Add(AssetDatabase.LoadAssetAtPath(assetPath, typeof(T)) as T);
                }

                return true;
            }
            else
                return false;
        }

        public static bool GetAssetFilesDictionary <T> (out Dictionary<string, T> resultDic) where T : RTSEngineScriptableObject
        {
            resultDic = new Dictionary<string, T>();

            resultDic.Add("Unassigned", null);

            if (AssetFiles == null)
                ReloadRTSPrefabsAndAssetFiles();

            AssetFiles.TryGetValue(typeof(T), out IEnumerable<RTSEngineScriptableObject> cached);

            if (cached == null)
                return true;

            foreach(T t in cached)
            {
                if (t == null)
                    continue;

                if (resultDic.ContainsKey(t.Key))
                {
                    Debug.LogError($"[RTSEditorHelper] '{t.Key}' is a duplicate key for the '{typeof(T).ToString()}' type in '{t.name}' and '{resultDic[t.Key].name}' asset files.", t);
                    return false;
                }

                resultDic.Add(t.Key, t);
            }

            return true;
        }

        public static string[] GetMatchingStrings (string searchInput, IReadOnlyList<string> searchTargets, IReadOnlyList<string> exceptions, System.Func<string, bool> condition = null)
        {
            List<string> matches = new List<string>();

            string targetPattern = $"{searchInput}";

            for (int i = 0; i < searchTargets.Count; i++)
            {
                string currTarget = searchTargets[i];

                if (!string.IsNullOrEmpty(currTarget) 
                    && !exceptions.Any(exception => exception == currTarget)
                    && System.Text.RegularExpressions.Regex.IsMatch(currTarget, targetPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    && (condition == null || condition.Invoke(currTarget)))
                    matches.Add(currTarget);
            }

            return matches
                .OrderBy(match => match)
                .ToArray();
        }
        #endregion

        #region Entities
        private static IDictionary<string, IEntity> Entities = null;

        public static IDictionary<string, IEntity> GetEntities()
        {
            if(Entities == null)
                ReloadRTSPrefabsAndAssetFiles();

            return Entities;
        }

        private static IDictionary<string, IEnumerable<IEntity>> EntitiesPerCategory = null;

        public static IDictionary<string, IEnumerable<IEntity>> GetEntityPrefabsPerCategory()
        {
            if (EntitiesPerCategory == null)
                ReloadRTSPrefabsAndAssetFiles();

            return EntitiesPerCategory;
        }

        public static void FetchEntityPrefabs ()
        {
            bool allValid = true;

            IEnumerable<IEntity> entityPrefabs = UnityEngine.Resources.LoadAll("Prefabs", typeof(GameObject))
                .Cast<GameObject>()
                .Where(obj => obj.GetComponent<IEntity>() != null)
                .Select(obj => obj.GetComponent<IEntity>());

            Entities = new Dictionary<string, IEntity>();
            EntitiesPerCategory = new Dictionary<string, IEnumerable<IEntity>>();

            foreach(IEntity entity in entityPrefabs)
            {
                if (GetEntities().ContainsKey(entity.Code))
                {
                    Debug.LogError($"[RTSEditorHelper] Failed to cache entity prefab {entity.gameObject.name}: Entity code '{entity.Code}' has been already used on another entity prefab (Prefab name: '{GetEntities()[entity.Code].gameObject.name}')", entity.gameObject);
                    allValid = false;
                }
                else
                    GetEntities().Add(entity.Code, entity);

                foreach (string category in entity.Category)
                {
                    if (GetEntityPrefabsPerCategory().ContainsKey(category))
                        GetEntityPrefabsPerCategory()[category] = GetEntityPrefabsPerCategory()[category].Append(entity);
                    else
                        GetEntityPrefabsPerCategory().Add(category, Enumerable.Repeat(entity, 1));
                }
            }

            if (allValid)
                Debug.Log("[RTSEditorHelper] Cached entity prefabs placed in a path that ends with '*/Resources/Prefabs'. All cached entities passed the validity tests.");
            else
                Debug.LogError("[RTSEditorHelper] Cached entity prefabs placed in a path that ends with '*/Resources/Prefabs' are not all valid. Please fix the above errors!");
        }
        #endregion

        #region Layers
        private static IReadOnlyDictionary<int, string> DemoLayers => new Dictionary<int, string>()
        {
            {8, "GroundTerrain" },
            {9, "Obstacle" },
            {10, "EntitySelection" },
            {11, "BaseTerrain" },

            {13, "Border" },

            {18, "MinimapUI" },
            {19, "HoverHealthBar" },
            {20, "MvtTargetObj" },
            {21, "AllCams" },

            {23, "AirTerrain" },
            {24, "PostProcessing" },
            {25, "A*GridObstacle" }
        };

        public static bool ImportPresetLayers()
        {
            bool success =  DemoLayers.All(kvp => CreateLayer(kvp.Key, kvp.Value));
            if(success)
                Debug.Log("[RTSEditorHelper] Preset layers have been successfully imported!");
            else
                Debug.Log("[RTSEditorHelper] There was an error in importing preset layers. Please contact the developer!");

            return success;
        }

        private static bool CreateLayer(int layerID, string layerName)
        {
            // Open tag manager
            SerializedObject tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            // Layers Property
            SerializedProperty layersProp = tagManager.FindProperty("layers");
            SerializedProperty sp = layersProp.GetArrayElementAtIndex(layerID);
            sp.stringValue = layerName;
            tagManager.ApplyModifiedProperties();
            return true;
        }
        #endregion

        #region Custom Inspector Utilities
        public static string[] EntitySearchExceptions = new string[] { 
            "new_unit_code",
            "new_building_code",
            "new_resource_code",
            "new_resource_building_code"
        };

        public static GUIStyle EditorTitleStyle => new GUIStyle(GUI.skin.label) {
            alignment = TextAnchor.MiddleCenter,
            fontStyle = FontStyle.Bold,
            fontSize = 14
        };
        #endregion

        #region Logging
        public static void Log(string msg, GameObject source = null)
        {
            Debug.Log($"*RTS Engine* {msg}", source);
        }

        public static void LogError(string msg, GameObject source = null)
        {
            Debug.LogError($"*RTS Engine* {msg}", source);
        }
        #endregion
    }
}
