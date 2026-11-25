using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace WalkingEyeExperiment.Editor
{
    [InitializeOnLoad]
    public static class ExperimentMenuChecks
    {
        const string StateKey="WalkingEyeExperiment.MenuValidationState";
        static readonly List<string> findings=new List<string>();
        static double startedAt;

        static ExperimentMenuChecks()
        { if(SessionState.GetInt(StateKey,0)>0) EditorApplication.update+=Tick; }

        [MenuItem("Experiment Integration/Validate Experiment Menu")]
        public static void Run()
        {
            EditorSceneManager.OpenScene(IntegrationBuild.ScenePath);
            var neon=GameObject.Find("Official Neon XR"); if(neon) neon.SetActive(false);
            var gaze=UnityEngine.Object.FindFirstObjectByType<NeonGazeBridge>(); gaze.useSyntheticGaze=true;
            var logger=UnityEngine.Object.FindFirstObjectByType<ResearchSessionLogger>(); logger.allowSyntheticRecording=true; logger.autoStart=false;
            var platform=UnityEngine.Object.FindFirstObjectByType<SeatedWalkingPlatform>();
            platform.requireStartMenu=true; platform.requireValidGazeToStart=false; platform.showStatus=true;
            findings.Clear();
            startedAt=EditorApplication.timeSinceStartup;
            SessionState.SetInt(StateKey,1);
            EditorApplication.update-=Tick; EditorApplication.update+=Tick;
            EditorApplication.isPlaying=true;
        }

        static void Require(bool value,string message)
        { if(!value) throw new Exception(message); findings.Add("PASS: "+message); }

        static void Tick()
        {
            int state=SessionState.GetInt(StateKey,0);
            if(state==0) { EditorApplication.update-=Tick; return; }
            if(!EditorApplication.isPlaying)
            {
                if(state==2) { SessionState.SetInt(StateKey,0); EditorApplication.Exit(0); }
                return;
            }
            if(EditorApplication.timeSinceStartup-startedAt>45) Fail(new Exception("Experiment menu check timed out"));
            if(state!=1 || Time.unscaledTime<1) return;
            try
            {
                var platform=UnityEngine.Object.FindFirstObjectByType<SeatedWalkingPlatform>();
                Require(!platform.ExperimentStarted && platform.AvatarInstance==null,"Avatar and trial wait for the Start action");
                Require(!platform.logger.Recording,"CSV recording waits for participant confirmation");
                var menu=GameObject.Find("Experiment Start Menu");
                Require(menu && menu.activeInHierarchy,"Participant start menu is visible before the trial");
                platform.SetParticipantNumber(42);
                Require(SeatedWalkingPlatform.FormatParticipantId(platform.participantNumber)=="P042","Participant number is normalized as P042");
                platform.SetTunnelFov(30);
                Require(platform.selectedTunnelFov==30,"Start menu selects either the 15 or 30 degree tunnel");
                platform.BeginExperiment();
                Require(platform.ExperimentStarted && platform.AvatarInstance,"Start creates the existing avatar and begins the experiment");
                Require(platform.logger.Recording && platform.logger.participantId=="P042" && platform.logger.trialId=="platform_1","Selected participant number and first trial are recorded");
                Require(Mathf.Approximately(platform.gaze.rpField.overlay.tunnelFovDegrees,30),"Selected tunnel field is applied before recording the trial");
                Require(platform.AvatarInstance.GetComponent<SimpleWalkDriver>().IsWalking,"Start begins avatar walking without a second button press");
                Require(!platform.gaze.debugVisualization && !platform.showStatus && !platform.headsetStatus.gameObject.activeInHierarchy && !menu.activeInHierarchy,"Menu, status and gaze debug visuals are hidden during the trial");
                int activeText=platform.participantCamera.GetComponentsInChildren<Text>(true).Count(t=>t.gameObject.activeInHierarchy);
                Require(activeText==0,"No headset text remains visible during the experiment");
                platform.logger.StopRecording();
                Directory.CreateDirectory("IntegrationEvidence");
                File.WriteAllLines("IntegrationEvidence/experiment-menu-checks.txt",findings);
                Debug.Log("INTEGRATION_EXPERIMENT_MENU_PASS");
                SessionState.SetInt(StateKey,2); EditorApplication.isPlaying=false;
            }
            catch(Exception e) { Fail(e); }
        }

        static void Fail(Exception e)
        {
            Directory.CreateDirectory("IntegrationEvidence");
            File.WriteAllLines("IntegrationEvidence/experiment-menu-checks.txt",findings.Concat(new[]{"FAIL: "+e}));
            Debug.LogException(e); SessionState.SetInt(StateKey,0); EditorApplication.Exit(1);
        }
    }
}
