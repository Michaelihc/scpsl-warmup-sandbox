using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AdminToys;
using CommandSystem;
using Mirror;
using NorthwoodLib;
using RemoteAdmin;
using UnityEngine;
using VoiceChat.Codec;
using VoiceChat.Codec.Enums;
using VoiceChat.Networking;
using Object = UnityEngine.Object;

namespace ScpslPluginStarter;

internal static class ServerAudioPlaybackService
{
    private const int SampleRate = 48000;
    private const int FrameSamples = 480;
    private const int FrameDurationMs = 10;
    private const float DefaultMaxDistance = 100000f;
    private static readonly ActionDispatcher? MainThreadActions = typeof(MainThreadDispatcher)
        .GetField("UpdateDispatcher", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?
        .GetValue(null) as ActionDispatcher;
    private static readonly object StateLock = new();
    private static SpeakerToy? _speaker;
    private static int _playbackToken;

    public static bool IsPlaying { get; private set; }

    public static bool TryStartWav(
        PluginConfig config,
        string requestedPath,
        ICommandSender sender,
        float? requestedVolume,
        out string response)
    {
        if (!config.ServerAudio.Enabled)
        {
            response = WarmupLocalization.T("Server audio is disabled in config.", "服务器音频已在配置中关闭。");
            return false;
        }

        if (!TryResolveAudioPath(config.ServerAudio, requestedPath, out string audioPath, out response))
        {
            return false;
        }

        if (!TryReadWav(audioPath, config.ServerAudio.MaxDurationSeconds, out float[] samples, out response))
        {
            return false;
        }

        float volume = Mathf.Clamp(requestedVolume ?? config.ServerAudio.DefaultVolume, 0f, 2f);
        int token;
        lock (StateLock)
        {
            token = ++_playbackToken;
            IsPlaying = true;
        }

        string displayName = Path.GetFileName(audioPath);
        Dispatch(() =>
        {
            if (!EnsureSpeaker(config.ServerAudio, sender, volume, out string speakerError))
            {
                lock (StateLock)
                {
                    if (_playbackToken == token)
                    {
                        IsPlaying = false;
                    }
                }

                LabApi.Features.Console.Logger.Warn($"[WarmupSandbox] Server audio failed to create speaker: {speakerError}");
            }
        });

        Task.Run(async () => await RunPlayback(samples, config.ServerAudio.SpeakerControllerId, token).ConfigureAwait(false));
        response = WarmupLocalization.T(
            $"Playing '{displayName}' serverwide ({samples.Length / (float)SampleRate:0.0}s, volume {volume:0.##}).",
            $"正在全服播放 '{displayName}'（{samples.Length / (float)SampleRate:0.0} 秒，音量 {volume:0.##}）。");
        return true;
    }

    public static bool Stop(out string response)
    {
        lock (StateLock)
        {
            _playbackToken++;
            IsPlaying = false;
        }

        Dispatch(DestroySpeaker);
        response = WarmupLocalization.T("Server audio stopped.", "全服音频已停止。");
        return true;
    }

    private static async Task RunPlayback(float[] samples, byte controllerId, int token)
    {
        await Task.Delay(150).ConfigureAwait(false);

        using OpusEncoder encoder = new(OpusApplicationType.Audio);
        float[] frame = new float[FrameSamples];
        byte[] encoded = new byte[512];
        int sentFrames = 0;

        for (int offset = 0; offset < samples.Length; offset += FrameSamples)
        {
            lock (StateLock)
            {
                if (_playbackToken != token)
                {
                    return;
                }
            }

            Array.Clear(frame, 0, frame.Length);
            int copyLength = Math.Min(FrameSamples, samples.Length - offset);
            Array.Copy(samples, offset, frame, 0, copyLength);

            int encodedLength = encoder.Encode(frame, encoded, FrameSamples);
            byte[] packet = new byte[encodedLength];
            Array.Copy(encoded, packet, encodedLength);

            Dispatch(() =>
            {
                lock (StateLock)
                {
                    if (_playbackToken != token)
                    {
                        return;
                    }
                }

                NetworkServer.SendToReady(new AudioMessage(controllerId, packet, packet.Length), Channels.Unreliable);
            });

            sentFrames++;
            long targetElapsedMs = sentFrames * FrameDurationMs;
            long actualElapsedMs = sentFrames == 1 ? 0 : (sentFrames - 1) * FrameDurationMs;
            int delayMs = (int)Math.Max(1, targetElapsedMs - actualElapsedMs);
            await Task.Delay(delayMs).ConfigureAwait(false);
        }

        lock (StateLock)
        {
            if (_playbackToken == token)
            {
                IsPlaying = false;
            }
        }

        Dispatch(() =>
        {
            lock (StateLock)
            {
                if (_playbackToken == token)
                {
                    DestroySpeaker();
                }
            }
        });
    }

    private static bool EnsureSpeaker(ServerAudioConfig config, ICommandSender sender, float volume, out string error)
    {
        if (!NetworkServer.active)
        {
            error = "NetworkServer is not active.";
            return false;
        }

        if (_speaker != null && _speaker.gameObject != null)
        {
            ConfigureSpeaker(_speaker, config.SpeakerControllerId, volume);
            error = string.Empty;
            return true;
        }

        if (!TryFindSpeakerPrefab(out SpeakerToy? prefab) || prefab == null)
        {
            error = "Speaker admin toy prefab was not found.";
            return false;
        }

        SpeakerToy speaker = Object.Instantiate(prefab);
        ConfigureSpeaker(speaker, config.SpeakerControllerId, volume);

        if (sender is PlayerCommandSender playerCommandSender)
        {
            speaker.OnSpawned(playerCommandSender.ReferenceHub, new ArraySegment<string>(Array.Empty<string>()));
        }
        else
        {
            NetworkServer.Spawn(speaker.gameObject);
        }

        _speaker = speaker;
        error = string.Empty;
        return true;
    }

    private static void ConfigureSpeaker(SpeakerToy speaker, byte controllerId, float volume)
    {
        speaker.NetworkControllerId = controllerId;
        speaker.NetworkIsSpatial = false;
        speaker.NetworkVolume = volume;
        speaker.NetworkMinDistance = 1f;
        speaker.NetworkMaxDistance = DefaultMaxDistance;
        speaker.NetworkIsStatic = true;
        speaker.transform.position = Vector3.zero;
        speaker.transform.localScale = Vector3.one;
    }

    private static bool TryFindSpeakerPrefab(out SpeakerToy? prefab)
    {
        foreach (GameObject candidate in NetworkClient.prefabs.Values)
        {
            if (candidate != null && candidate.TryGetComponent(out prefab))
            {
                return true;
            }
        }

        prefab = null;
        return false;
    }

    private static void DestroySpeaker()
    {
        if (_speaker == null)
        {
            return;
        }

        GameObject gameObject = _speaker.gameObject;
        _speaker = null;
        if (gameObject == null)
        {
            return;
        }

        if (NetworkServer.active)
        {
            NetworkServer.Destroy(gameObject);
        }

        Object.Destroy(gameObject);
    }

    private static void Dispatch(Action action)
    {
        if (MainThreadActions == null)
        {
            action.Invoke();
            return;
        }

        MainThreadActions.Dispatch(action);
    }

    private static bool TryResolveAudioPath(
        ServerAudioConfig config,
        string requestedPath,
        out string audioPath,
        out string response)
    {
        string trimmedPath = requestedPath.Trim().Trim('"');
        foreach (string candidate in EnumerateAudioPathCandidates(config, trimmedPath))
        {
            if (File.Exists(candidate))
            {
                audioPath = candidate;
                response = string.Empty;
                return true;
            }
        }

        audioPath = string.Empty;
        response = WarmupLocalization.T(
            $"Audio file '{requestedPath}' was not found. Put .wav files in LabAPI configs/*/WarmupSandbox/{config.AudioDirectoryName}.",
            $"找不到音频文件 '{requestedPath}'。请把 .wav 放到 LabAPI configs/*/WarmupSandbox/{config.AudioDirectoryName}。");
        return false;
    }

    private static IEnumerable<string> EnumerateAudioPathCandidates(ServerAudioConfig config, string requestedPath)
    {
        if (Path.IsPathRooted(requestedPath))
        {
            yield return requestedPath;
            yield break;
        }

        yield return Path.GetFullPath(requestedPath);

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            yield break;
        }

        string configRoot = Path.Combine(appData, "SCP Secret Laboratory", "LabAPI", "configs");
        if (!Directory.Exists(configRoot))
        {
            yield break;
        }

        string globalPath = Path.Combine(configRoot, "global", "WarmupSandbox", config.AudioDirectoryName, requestedPath);
        yield return globalPath;

        foreach (string portDirectory in Directory.GetDirectories(configRoot))
        {
            yield return Path.Combine(portDirectory, "WarmupSandbox", config.AudioDirectoryName, requestedPath);
        }
    }

    private static bool TryReadWav(string path, int maxDurationSeconds, out float[] samples, out string response)
    {
        samples = Array.Empty<float>();
        response = string.Empty;

        try
        {
            using FileStream stream = File.OpenRead(path);
            using BinaryReader reader = new(stream);
            if (new string(reader.ReadChars(4)) != "RIFF")
            {
                response = "Only RIFF/WAVE .wav files are supported.";
                return false;
            }

            reader.ReadInt32();
            if (new string(reader.ReadChars(4)) != "WAVE")
            {
                response = "Only RIFF/WAVE .wav files are supported.";
                return false;
            }

            ushort audioFormat = 0;
            ushort channels = 0;
            int sampleRate = 0;
            ushort bitsPerSample = 0;
            byte[]? data = null;

            while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
            {
                string chunkId = new(reader.ReadChars(4));
                int chunkSize = reader.ReadInt32();
                long chunkEnd = reader.BaseStream.Position + chunkSize;

                if (chunkId == "fmt ")
                {
                    audioFormat = reader.ReadUInt16();
                    channels = reader.ReadUInt16();
                    sampleRate = reader.ReadInt32();
                    reader.ReadInt32();
                    reader.ReadUInt16();
                    bitsPerSample = reader.ReadUInt16();
                }
                else if (chunkId == "data")
                {
                    data = reader.ReadBytes(chunkSize);
                }

                reader.BaseStream.Position = Math.Min(chunkEnd + (chunkSize % 2), reader.BaseStream.Length);
            }

            if (audioFormat != 1 || bitsPerSample != 16 || channels == 0 || sampleRate <= 0 || data == null)
            {
                response = "Only 16-bit PCM .wav files are supported.";
                return false;
            }

            float durationSeconds = data.Length / (float)(channels * sizeof(short) * sampleRate);
            if (maxDurationSeconds > 0 && durationSeconds > maxDurationSeconds)
            {
                response = $"Audio is {durationSeconds:0.0}s, above the configured max of {maxDurationSeconds}s.";
                return false;
            }

            samples = ConvertPcm16ToMono48k(data, channels, sampleRate);
            return true;
        }
        catch (Exception exception)
        {
            response = $"Failed to load audio: {exception.Message}";
            return false;
        }
    }

    private static float[] ConvertPcm16ToMono48k(byte[] data, ushort channels, int sourceRate)
    {
        int sourceFrames = data.Length / (channels * sizeof(short));
        float[] mono = new float[sourceFrames];
        for (int frameIndex = 0; frameIndex < sourceFrames; frameIndex++)
        {
            float sum = 0f;
            for (int channel = 0; channel < channels; channel++)
            {
                int byteIndex = ((frameIndex * channels) + channel) * sizeof(short);
                sum += BitConverter.ToInt16(data, byteIndex) / 32768f;
            }

            mono[frameIndex] = Mathf.Clamp(sum / channels, -1f, 1f);
        }

        if (sourceRate == SampleRate)
        {
            return mono;
        }

        int targetFrames = Math.Max(1, (int)Math.Round(mono.Length * (SampleRate / (double)sourceRate)));
        float[] resampled = new float[targetFrames];
        double ratio = sourceRate / (double)SampleRate;
        for (int i = 0; i < targetFrames; i++)
        {
            double sourcePosition = i * ratio;
            int left = Math.Min((int)sourcePosition, mono.Length - 1);
            int right = Math.Min(left + 1, mono.Length - 1);
            float t = (float)(sourcePosition - left);
            resampled[i] = Mathf.Lerp(mono[left], mono[right], t);
        }

        return resampled;
    }
}
