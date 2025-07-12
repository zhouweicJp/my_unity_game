﻿using UnityEditor;

using RTSEngine.Health;
using RTSEngine.Utilities;
using UnityEngine;
using System;

namespace RTSEngine.EditorOnly.Health
{
    [CustomEditor(typeof(UnitHealth))]
    public class UnitEditor : EntityHealthEditor<UnitHealth>
    {
        protected override void OnGeneralInspectorGUI()
        {
            base.OnGeneralInspectorGUI();

            EditorGUILayout.PropertyField(SO.FindProperty("stopMovingOnDamage"));
        }
    }

    [CustomEditor(typeof(BuildingHealth))]
    public class BuildingEditor : EntityHealthEditor<BuildingHealth>
    {
        protected override void OnGeneralInspectorGUI()
        {
            base.OnGeneralInspectorGUI();

            EditorGUILayout.Space();

            EditorGUILayout.HelpBox("The 'Build Time' field is only relevant if the 'Construction Type' is set to 'Time' in the Building Manager component in the map scene.", MessageType.Warning);
            EditorGUILayout.PropertyField(SO.FindProperty("buildTime"));

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(SO.FindProperty("repairCosts"));
        }

        protected override void OnHealthStatesInspectorGUI()
        {
            EditorGUILayout.PropertyField(SO.FindProperty("constructionStates"));
            EditorGUILayout.PropertyField(SO.FindProperty("constructionCompleteState"));

            EditorGUILayout.Space();

            base.OnHealthStatesInspectorGUI();
        }
    }

    [CustomEditor(typeof(ResourceHealth))]
    public class ResourceEditor : EntityHealthEditor<ResourceHealth>
    {
        protected override void OnHealthStatesInspectorGUI()
        {
            base.OnHealthStatesInspectorGUI();

            EditorGUILayout.Space();

            EditorGUILayout.PropertyField(SO.FindProperty("collectedState"));
        }
    }

    public class EntityHealthEditor<T> : TabsEditorBase<T> where T : EntityHealth
    {
        protected override Int2D tabID {
            get => comp.tabID;
            set => comp.tabID = value;
        }

        private string[][] toolbars = new string[][] {
            new string[] {"General", "Destruction", "Health States", "Debug" }
        };

        public override void OnInspectorGUI()
        {
            OnInspectorGUI(toolbars);
        }

        protected override void OnTabSwitch(string tabName)
        {
            switch (tabName)
            {
                case "General":
                    OnGeneralInspectorGUI();
                    break;
                case "Destruction":
                    OnDestructionInspectorGUI();
                    break;
                case "Health States":
                    OnHealthStatesInspectorGUI();
                    break;
                case "Debug":
                    OnDebugInspectorGUI();
                    break;
            }
        }

        protected virtual void OnGeneralInspectorGUI()
        {
            EditorGUILayout.PropertyField(SO.FindProperty("maxHealth"));
            EditorGUILayout.PropertyField(SO.FindProperty("initialHealth"));

            EditorGUILayout.Space();

            EditorGUILayout.PropertyField(SO.FindProperty("canIncrease"));
            EditorGUILayout.PropertyField(SO.FindProperty("canDecrease"));
            if(SO.FindProperty("canBeAttacked") != null)
                EditorGUILayout.PropertyField(SO.FindProperty("canBeAttacked"));
            if(SO.FindProperty("attackTargetPosition") != null)
                EditorGUILayout.PropertyField(SO.FindProperty("attackTargetPosition"));

            EditorGUILayout.Space();

            float legacyHHBYVal = SO.FindProperty("hoverHealthBarY").floatValue;
            if (legacyHHBYVal != -1.0f && SO.FindProperty("hoverHealthBarData.offset").vector3Value.y == -1.0f)
                SO.FindProperty("hoverHealthBarData.offset").vector3Value = new Vector3(0.0f, legacyHHBYVal, 0.0f);

            EditorGUILayout.PropertyField(SO.FindProperty("hoverHealthBarData"));

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(SO.FindProperty("hitEffect"));
            EditorGUILayout.PropertyField(SO.FindProperty("hitAudio"));
        }

        protected virtual void OnDestructionInspectorGUI()
        {
            EditorGUILayout.PropertyField(SO.FindProperty("destroyObject"));
            EditorGUILayout.PropertyField(SO.FindProperty("destroyObjectDelay"));

            EditorGUILayout.Space();

            EditorGUILayout.PropertyField(SO.FindProperty("destroyAward"));

            EditorGUILayout.Space();

            EditorGUILayout.PropertyField(SO.FindProperty("destructionEffect"));
            EditorGUILayout.PropertyField(SO.FindProperty("destructionAudio"));
        }

        protected virtual void OnHealthStatesInspectorGUI()
        {
            EditorGUILayout.PropertyField(SO.FindProperty("states"));
            EditorGUILayout.PropertyField(SO.FindProperty("destroyState"));
        }

        protected virtual void OnDebugInspectorGUI()
        {
            EditorGUILayout.IntField("Current Health", comp.CurrHealth);
        }

    }
}
