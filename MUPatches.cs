using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Reptile;
using Reptile.Phone;
using UnityEngine;
using TMPro;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;

namespace MusicCurator {
    [HarmonyPatch(typeof(Reptile.Phone.Phone))]
    internal class PhonePatches {
        [HarmonyPostfix]
        [HarmonyPatch(nameof(Reptile.Phone.Phone.TurnOff))]
        public static void TurnOffPostfix_ResetAppOptions() {
            MusicCuratorPlugin.selectedPlaylist = -1;
            MusicCuratorPlugin.appSelectedTrack = -1;
        } 
    }

    [HarmonyPatch(typeof(MusicPlayer))]
    internal class MusicPlayerPatches {
        [HarmonyPostfix]
        [HarmonyPatch(nameof(MusicPlayer.EvaluateRepeatingMusicTrack))]
        public static void EVRPostfix(MusicPlayer __instance, ref bool __result) {
            if (MusicCuratorPlugin.skipping || (MCSettings.skipRepeatInPlaylists.Value && MusicCuratorPlugin.playlistTracks.Contains(__instance.musicTrackQueue.CurrentMusicTrack))) { __result = false; }
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(MusicPlayer.PlayNext))]
        //[HarmonyPriority(Priority.Low)] //[HarmonyAfter(["com.dragsun.Shufleify"])] 
        public static void PlayNextPostfix_OverrideNextTrack(MusicPlayer __instance) {
            // looping single track
            if (MusicCuratorPlugin.loopingSingleTrackIndex >= 0) {
                MusicTrack nextTrack = __instance.GetMusicTrack(MusicCuratorPlugin.loopingSingleTrackIndex);
                if (!MusicCuratorPlugin.IsInvalidTrack(nextTrack)) { MusicCuratorPlugin.PlayTrack(nextTrack); }
                else { // stop looping single track
                    MusicCuratorPlugin.loopingSingleTrackIndex = -1;
                    MusicCuratorPlugin.SetAppShuffle(true);
                }
            }

            // has next tracks queued
            else if (MusicCuratorPlugin.queuedTracks.Any()) { 
                MusicTrack nextTrack = MusicCuratorPlugin.queuedTracks[0];
                MusicCuratorPlugin.PlayTrack(nextTrack);
                MusicCuratorPlugin.queuedTracks.RemoveAt(0);
            } 

            // has playlist tracks
            else if (MusicCuratorPlugin.playlistTracks.Any()) {
                MusicTrack nextTrack = MusicCuratorPlugin.playlistTracks[0];
                
                // if can't play this track...
                bool cantPlay = MusicCuratorPlugin.IsInvalidTrack(nextTrack) || !__instance.musicTrackQueue.currentMusicTracks.Contains(nextTrack) || (!MCSettings.playlistTracksNoExclude.Value && MusicCuratorPlugin.excludedTracks.Contains(nextTrack));
                if (cantPlay) {
                    // try to find valid track in playlist
                    var i = 0;
                    while (cantPlay && i <= MusicCuratorPlugin.playlistTracks.Count) {
                        i++;
                        MusicCuratorPlugin.playlistTracks.RemoveAt(0);
                        MusicCuratorPlugin.playlistTracks.Add(nextTrack);
                        nextTrack = MusicCuratorPlugin.playlistTracks[0];
                        cantPlay = MusicCuratorPlugin.IsInvalidTrack(nextTrack) || !__instance.musicTrackQueue.currentMusicTracks.Contains(nextTrack) || (!MCSettings.playlistTracksNoExclude.Value && MusicCuratorPlugin.excludedTracks.Contains(nextTrack));
                    }
                    
                    // if failed to find valid track, stop playing the playlist and give up
                    if (i > MusicCuratorPlugin.playlistTracks.Count) {
                        MusicCuratorPlugin.playlistTracks.Clear();
                        MusicCuratorPlugin.currentPlaylistIndex = -1;
                        return;
                    }
                }

                MusicCuratorPlugin.PlayTrack(nextTrack);   
                MusicCuratorPlugin.playlistTracks.RemoveAt(0);
                MusicCuratorPlugin.playlistTracks.Add(nextTrack);
                // re-shuffle music tracks if need be
                if (nextTrack == MusicCuratorPlugin.playlistStartingTrack && MCSettings.reshuffleOnLoop.Value && MusicCuratorPlugin.musicPlayer.shuffle) {
                    MusicCuratorPlugin.LoadPlaylistIntoQueue(MusicCuratorPlugin.currentPlaylistIndex, MusicCuratorPlugin.shufflingPlaylist);
                    if (MusicCuratorPlugin.playlistTracks[0] == nextTrack) {
                        MusicCuratorPlugin.playlistTracks.RemoveAt(0);
                        MusicCuratorPlugin.playlistTracks.Add(nextTrack);
                    }
                }
            }

            // handle excluded tracks (moved to trackqueue)
            //else if (MusicCuratorPlugin.excludedTracks.Contains(__instance.musicTrackQueue.CurrentMusicTrack) && !MusicCuratorPlugin.ContinuingStageTrack) {
            //    MusicCuratorPlugin.SkipCurrentTrack();
            //} 
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(MusicPlayer.PlayFrom))]
        public static void PlayFromPostfix_PlaylistHandling(int index, int playbackSamples, MusicPlayer __instance) {
            // clear the queue if the player chose this track manually
            if (!MusicCuratorPlugin.ContinuingStageTrack && MusicCuratorPlugin.PlayerUsingMusicApp()) { 
                MusicCuratorPlugin.queuedTracks.Clear();
                MusicCuratorPlugin.playlistTracks.Clear();
                MusicCuratorPlugin.currentPlaylistIndex = -1;
            //} else {
                //MusicCuratorPlugin.ContinuingStageTrack = false;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(MusicPlayer.SetShuffle))]
        public static void SetShufflePostfix_PlaylistShuffle(bool value, MusicPlayer __instance) {
            if (value != MusicCuratorPlugin.shufflingPlaylist) {
                MusicCuratorPlugin.shufflingPlaylist = value;
                MusicCuratorPlugin.ReorderPlaylistInQueue(value);
            }
        }
    }

    [HarmonyPatch(typeof(MusicTrackQueue))]
    internal class MusicQueuePatches {
        [HarmonyPostfix]
        [HarmonyPatch(nameof(MusicTrackQueue.ClearTracks))] // find out missing stage mixtape, and add it if using brr
        public static void ClearTracksPostfix_AllMixtapes(MusicTrackQueue __instance) {
            if (!MCSettings.allMixtapes.Value) { return; }
            MusicTrack hideoutTrack = Core.Instance.AudioManager.MusicLibraryPlayer.GetMusicTrackByID(MusicTrackID.Hideout_Mixtape);
            MusicTrack chapterTrack = Core.Instance.chapterMusic.GetChapterMusic(Story.GetCurrentObjectiveInfo().chapter);
            MusicCuratorPlugin.missingStageTrack = Reptile.Utility.GetCurrentStage() == Stage.hideout ? chapterTrack : hideoutTrack;
            if (PlaylistSaveData.excludedTracksCarryOver.Contains(MusicCuratorPlugin.TrackToSongID(MusicCuratorPlugin.missingStageTrack))) { 
                MusicCuratorPlugin.excludedTracks.Add(MusicCuratorPlugin.missingStageTrack); 
                //MusicCuratorPlugin.SaveExclusions();
            }
            
            if (MusicCuratorPlugin.hasBRR && MusicCuratorPlugin.missingStageTrack != null) { 
                BRRHelper.AddMissingTrackToAudios(MusicCuratorPlugin.missingStageTrack); 
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(MusicTrackQueue.UpdateMusicQueueForStage))] // adding missing stage mixtake (non-brr version)
        public static bool RefreshingPrefix_AllMixtapes(MusicTrack trackToPlay, MusicTrackQueue __instance) {
            if (MusicCuratorPlugin.missingStageTrack == null || MusicCuratorPlugin.hasBRR || __instance.currentMusicTracks.Contains(MusicCuratorPlugin.missingStageTrack) || !MCSettings.allMixtapes.Value) { return true; }

            __instance.currentMusicTracks.Add(MusicCuratorPlugin.missingStageTrack);
            if (PlaylistSaveData.excludedTracksCarryOver.Contains(MusicCuratorPlugin.TrackToSongID(MusicCuratorPlugin.missingStageTrack))) { 
                MusicCuratorPlugin.excludedTracks.Add(MusicCuratorPlugin.missingStageTrack); 
                //MusicCuratorPlugin.SaveExclusions();
            }

            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(MusicTrackQueue.SelectNextTrack))]
        public static bool NTPrefix_SkipBlocklisted(MusicTrackQueue __instance) {
            if (__instance.AmountOfTracks <= 0) { return false; }
            int nextInQueue = 0;
            int i = 0;
            int runs = 0;
            bool solved = false;

            while (runs < __instance.indexQueue.QueueCount)
            {
                runs++;
                i++;
                if (i + __instance.indexQueue.indexQueue.IndexOf(__instance.currentTrackIndex) >= __instance.indexQueue.indexQueue.Count) {
                    i = 0 - __instance.indexQueue.indexQueue.IndexOf(__instance.currentTrackIndex);
                }
                nextInQueue = __instance.indexQueue.GetNextInQueue(__instance.currentTrackIndex, i);
                
                bool dontSkipPlaylistTrack = MusicCuratorPlugin.currentPlaylistIndex != -1 && MusicCuratorPlugin.playlistTracks.Contains(__instance.GetMusicTrack(nextInQueue)) && MCSettings.playlistTracksNoExclude.Value; 
                if (!MusicCuratorPlugin.excludedTracks.Contains(__instance.GetMusicTrack(nextInQueue)) && !dontSkipPlaylistTrack)
                {
                    solved = true;
                    break;
                }
            }
            
            if (!solved) { nextInQueue = __instance.indexQueue.GetNextInQueue(__instance.currentTrackIndex, 1); }
            int previousInQueue = __instance.indexQueue.GetPreviousInQueue(__instance.currentTrackIndex, (int)__instance.nPreviousTracksBuffered);
            __instance.EvaluateNextTrack(nextInQueue);
            __instance.EvaluateTrackToUnload(previousInQueue);
            return false;
        }
    }

    [HarmonyPatch(typeof(MusicPlayerTrackButton))]
    internal class TrackButtonPatches {
        [HarmonyPostfix]
        [HarmonyPatch(nameof(MusicPlayerTrackButton.ConstantUpdate))]
        public static void ConstantUpdatePostfix(MusicPlayerTrackButton __instance) {
            if (!__instance.IsHidden) { MusicCuratorPlugin.UpdateButtonColor(__instance); } // TODO: find a better spot for this. updating this complicated process is very inefficient 
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(MusicPlayerTrackButton.SetMusicApp))]
        public static void SetupPostfix_CreateLabel(MusicPlayerTrackButton __instance) {
            TextMeshProUGUI queuePosLabel = __instance.m_TitleLabel.GetComponentsInChildren<TextMeshProUGUI>().LastOrDefault();
            if (queuePosLabel == null || queuePosLabel == __instance.m_TitleLabel) {
                // Create queue number label
                TextMeshProUGUI newLabel = UnityEngine.Object.Instantiate(__instance.m_TitleLabel, __instance.m_TitleLabel.transform);
                newLabel.margin = new Vector4 (0,0,0,0);
                newLabel.alignment = TextAlignmentOptions.Center;
                newLabel.text = "";
                newLabel.fontSize *= 2f;
                newLabel.transform.position = __instance.m_Disc.transform.position;
            }
        }
    }

    [HarmonyPatch(typeof(AppMusicPlayer))]
    internal class MusicAppPatches {
        [HarmonyPostfix]
        [HarmonyPatch(nameof(AppMusicPlayer.OnAppDisable))]
        public static void OnMusicAppDisable_SaveExclusions() {
            MusicCuratorPlugin.SaveExclusions();
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(AppMusicPlayer.ToggleShuffle))]
        public static bool ToggleShuffle_LoopSingleTrack(ref bool value, AppMusicPlayer __instance) {
            // if player is changing value manually (allow for regular toggling in other apps, or automatically, without butting in)...
            if (value != __instance.GameMusicPlayer.GetShuffle() && MusicCuratorPlugin.PlayerUsingMusicApp()) {
                // if looping track and player hits right, stop looping track and go back to regualr loop
                if (MusicCuratorPlugin.loopingSingleTrackIndex >= 0 && value == true) {
                    MusicCuratorPlugin.loopingSingleTrackIndex = -1;
                    value = false;
                } 
                // if shuffling and player hits right, start looping track
                else if (value == false && __instance.GameMusicPlayer.GetShuffle() == true) {
                    MusicCuratorPlugin.loopingSingleTrackIndex = __instance.GameMusicPlayer.CurrentTrackIndex;
                }
            }
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(AppMusicPlayer.SetShuffleIconImage))]
        public static void SetShuffleIconImagePostfix(AppMusicPlayer __instance) {
            if (MusicCuratorPlugin.loopingSingleTrackIndex >= 0) {
                __instance.shuffleIconImageSelected.sprite = (__instance.shuffleIconImageUnselected.sprite = (MusicCuratorPlugin.loopingSingleTrackSprite));
            }
        }
    }

    [HarmonyPatch(typeof(StageManager))]
    internal class SMPatches {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(StageManager), "StartMusicForStage", new Type[] { typeof(Stage), typeof(int) })]
        public static bool SMFSPrefix(ref Stage stage, int playbackSamples, StageManager __instance) {
            MusicCuratorPlugin.ContinuingStageTrack = true;
            //MusicAppPatches.stageManager = __instance;
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(StageManager), "StartMusicForStage", new Type[] { typeof(Stage), typeof(int) })]
        [HarmonyPriority(Priority.Low)] 
        public static void SMFSPostfix(Stage stage, int playbackSamples, StageManager __instance) {
            MusicCuratorPlugin.ContinuingStageTrack = false;
            MusicCuratorPlugin.LoadPlaylists(PlaylistSaveData.playlists);

            bool skip = MusicCuratorPlugin.excludedTracks.Contains((__instance.musicPlayer as MusicPlayer).musicTrackQueue.CurrentMusicTrack);
            if (MCSettings.instantShuffle.Value && (!MusicCuratorPlugin.hasInstantShuffledAlready || MCSettings.alwaysInstantShuffle.Value)) {
                skip = true;
                MusicCuratorPlugin.SetAppShuffle(true);
                MusicCuratorPlugin.hasInstantShuffledAlready = true;
            }
            if (skip) {
                MusicCuratorPlugin.SkipCurrentTrack();
            }

            MusicCuratorPlugin.LoadExclusions();
        }
    }

    [HarmonyPatch(typeof(BaseModule))]
    internal class ReloadMainMenu {
        [HarmonyPatch(nameof(BaseModule.LoadMainMenuScene))]
        [HarmonyPostfix]
        static void LoadingMenu_ResetVars() {
            MusicCuratorPlugin.resetVariables(); 
        }
    }

}