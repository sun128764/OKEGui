﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using OKEGui.JobProcessor;
using OKEGui.Model;
using OKEGui.Utils;

namespace OKEGui
{
    public enum EACProgressType
    {
        Analyze,
        Process,
        Completed,
    }

    public partial class EACDemuxer
    {
        public enum ProcessState
        {
            FetchStream,
            ExtractStream,
        }

        private struct EacOutputTrackType
        {
            public TrackCodec Codec { get; set; }
            public string RawOutput { get; set; }
            public string FileExtension { get; set; }
            public bool Extract { get; set; }

            public TrackType Type { get; set; }

            public EacOutputTrackType(TrackCodec codec, string rawOutput, string extension, bool extract, TrackType type)
            {
                Codec = codec;
                RawOutput = rawOutput;
                FileExtension = extension;
                Extract = extract;
                Type = type;
            }
        }

        private string _eacPath;
        private List<TrackInfo> tracks = new List<TrackInfo>();
        private Action<double, EACProgressType> _progressCallback;
        private Process proc = new Process();
        private ManualResetEvent mre = new ManualResetEvent(false);
        private ProcessState state;
        private string sourceFile;
        private List<AudioInfo> JobAudio;

        private static List<EacOutputTrackType> s_eacOutputs = new List<EacOutputTrackType> {
            new EacOutputTrackType(TrackCodec.RAW_PCM,    "RAW/PCM",          "flac",    true,  TrackType.Audio),
            new EacOutputTrackType(TrackCodec.DTSMA,      "DTS Master Audio", "flac",    true,  TrackType.Audio),
            new EacOutputTrackType(TrackCodec.TRUEHD_AC3, "TrueHD/AC3",       "flac",    true,  TrackType.Audio),
            new EacOutputTrackType(TrackCodec.TRUEHD_AC3, "TrueHD",           "flac",    true,  TrackType.Audio),
            new EacOutputTrackType(TrackCodec.AC3,        "AC3",              "ac3",     true,  TrackType.Audio),
            new EacOutputTrackType(TrackCodec.DTS,        "DTS",              "dts",     true,  TrackType.Audio),
            new EacOutputTrackType(TrackCodec.MPEG2,      "MPEG2",            "m2v",     false, TrackType.Video),
            new EacOutputTrackType(TrackCodec.H264_AVC,   "h264/AVC",         "h264",    false, TrackType.Video),
            new EacOutputTrackType(TrackCodec.PGS,        "Subtitle (PGS)",   "sup",     true,  TrackType.Subtitle),
            new EacOutputTrackType(TrackCodec.Chapter,    "Chapters",         "txt",     true,  TrackType.Chapter),
        };

        public EACDemuxer(string eacPath, string fileName, List<AudioInfo> jobAudio)
        {
            _eacPath = eacPath;
            sourceFile = fileName;
            JobAudio = new List<AudioInfo>();
            JobAudio.AddRange(jobAudio);
        }

        private void StartEac(string arguments, bool asyncRead)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _eacPath,
                Arguments = arguments + " -progressnumbers",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
            };

            proc.StartInfo = startInfo;
            try
            {
                proc.Start();
                if (asyncRead)
                {
                    new Thread(new ThreadStart(readStdErr)).Start();
                    new Thread(new ThreadStart(readStdOut)).Start();
                    proc.WaitForExit();
                }
                else
                {
                    proc.WaitForExit();
                    readStream(proc.StandardOutput);
                }
            }
            catch (Exception e) { throw e; }
        }

        private void readStream(StreamReader sr)
        {
            string line;
            if (proc != null)
            {
                try
                {
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (state == ProcessState.FetchStream)
                        {
                            DetectFileTracks(line);
                            _progressCallback(0, EACProgressType.Analyze);
                        }
                        else if (state == ProcessState.ExtractStream)
                        {
                            Regex rAnalyze = new Regex("analyze: ([0-9]+)%");
                            Regex rProgress = new Regex("process: ([0-9]+)%");

                            double p = 0;
                            if (rAnalyze.IsMatch(line))
                            {
                                double.TryParse(rAnalyze.Split(line)[1], out p);
                                if (p > 1)
                                {
                                    _progressCallback(p, EACProgressType.Analyze);
                                }
                            }
                            else if (rProgress.IsMatch(line))
                            {
                                double.TryParse(rProgress.Split(line)[1], out p);
                                if (p > 1)
                                {
                                    _progressCallback(p, EACProgressType.Process);
                                }
                            }

                            if (line.ToLower().Contains("done."))
                            {
                                _progressCallback(100, EACProgressType.Completed);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    System.Windows.MessageBox.Show(e.ToString());
                    throw e;
                }
            }
        }

        private void readStdOut()
        {
            StreamReader sr = null;
            try
            {
                sr = proc.StandardOutput;
            }
            catch (Exception e)
            {
                Debugger.Log(0, "", "Exception getting IO reader for stdout" + e.ToString());
                return;
            }
            readStream(sr);
        }

        private void readStdErr()
        {
            StreamReader sr = null;
            try
            {
                sr = proc.StandardError;
            }
            catch (Exception e)
            {
                Debugger.Log(0, "", "Exception getting IO reader for stderr" + e.ToString());
                return;
            }
            readStream(sr);
        }

        private TrackCodec EacOutputToTrackCodec(string str)
        {
            str = str.Trim();
            EacOutputTrackType outputType = s_eacOutputs.Find(val => val.RawOutput == str);
            return outputType.Codec;
        }

        private TrackType EacOutputToTrackType(string str)
        {
            str = str.Trim();
            EacOutputTrackType outputType = s_eacOutputs.Find(val => val.RawOutput == str);
            return outputType.Type;
        }

        private void DetectFileTracks(string line)
        {
            line.Trim();
            if (string.IsNullOrEmpty(line)) return;

            if (Regex.IsMatch(line, @"^\d*?: .*$"))
            {
                //原盘没有信息的PGS字幕
                if (line.Contains("PGS") && !line.Contains(","))
                    line += ", Japanese";

                var match = Regex.Match(line, @"^(\d*?): (.*?), (.*?)$");

                if (match.Groups.Count < 4)
                {
                    return;
                }

                var trackInfo = new TrackInfo
                {
                    Index = Convert.ToInt32(match.Groups[1].Value),
                    Codec = EacOutputToTrackCodec(match.Groups[2].Value),
                    Information = match.Groups[3].Value.Trim(),
                    RawOutput = line,
                    SourceFile = sourceFile,
                    Type = EacOutputToTrackType(match.Groups[2].Value),
                    DupOrEmpty = false
                };

                if (TrackCodec.Unknown == trackInfo.Codec)
                {
                    throw new ArgumentException($"不明类型: {trackInfo.RawOutput}");
                }
                tracks.Add(trackInfo);
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="completedCallback">抽取的轨道，不包含重复轨道；重复轨道文件名带有.bak</param>
        public MediaFile Extract(Action<double, EACProgressType> progressCallback)
        {
            if (!new FileInfo(sourceFile).Exists)
            {
                return null;
            }
            _progressCallback = progressCallback;

            state = ProcessState.FetchStream;
            StartEac($"\"{sourceFile}\"", false);

            var args = new List<string>();
            var extractResult = new List<TrackInfo>();
            List<TrackInfo> srcAudio = new List<TrackInfo>();

            foreach (TrackInfo track in tracks)
            {
                if (track.Type == TrackType.Audio)
                {
                    srcAudio.Add(track);
                }
            }
            if (srcAudio.Count != JobAudio.Count)
            {
                OKETaskException ex = new OKETaskException(Constants.audioNumMismatchSmr);
                ex.progress = 0.0;
                ex.Data["SRC_TRACK"] = srcAudio.Count;
                ex.Data["DST_TRACK"] = JobAudio.Count;
                throw ex;
            }
            int audioId = 0;
            foreach (TrackInfo track in tracks)
            {
                EacOutputTrackType trackType = s_eacOutputs.Find(val => val.Codec == track.Codec);
                if (!trackType.Extract)
                {
                    continue;
                };
                if (track.Type == TrackType.Audio)
                {
                    AudioInfo jobAudioInfo = JobAudio[audioId++];
                    if (jobAudioInfo.MuxOption == MuxOption.Skip)
                    {
                        continue;
                    }
                }

                args.Add($"{track.Index}:\"{track.OutFileName}\"");
                extractResult.Add(track);
            }
            JobAudio.RemoveAll(info => info.MuxOption == MuxOption.Skip);

            state = ProcessState.ExtractStream;
            StartEac($"\"{sourceFile}\" {string.Join(" ", args)}", true);

            foreach (TrackInfo track in extractResult)
            {
                FileInfo finfo = new FileInfo(track.OutFileName);
                if (!finfo.Exists || finfo.Length == 0)
                {
                    throw new Exception("文件输出失败: " + track.OutFileName);
                }
                else
                {
                    track.fileSize = finfo.Length;
                }
                if (track.Type == TrackType.Audio)
                {
                    FFmpegVolumeChecker checker = new FFmpegVolumeChecker(track.OutFileName);
                    checker.start();
                    checker.waitForFinish();
                    track.maxVolume = checker.MaxVolume;
                    track.meanVolume = checker.MeanVolume;
                }
            }

            List<int> removeList = new List<int>();
            for (int i = 0; i < extractResult.Count; i++)
            {
                TrackInfo track = extractResult[i];
                if (removeList.Contains(track.Index))
                {
                    continue;
                }
                if (track.IsEmpty())
                {
                    removeList.Add(track.Index);
                    track.MarkSkipping();
                    continue;
                }

                for (int j = i + 1; j < extractResult.Count; j++)
                {
                    TrackInfo other = extractResult[j];
                    if (track.IsDuplicate(other))
                    {
                        removeList.Add(other.Index);
                        other.MarkSkipping();
                    }
                }
            }

            MediaFile mf = new MediaFile();
            audioId = 0;
            foreach (var item in extractResult)
            {
                OKEFile file = new OKEFile(item.OutFileName);
                if (item.Type == TrackType.Audio)
                {
                    AudioInfo info = JobAudio[audioId++];
                    info.DupOrEmpty = item.DupOrEmpty;
                    mf.AddTrack(new AudioTrack(file, info));
                }
                else if (item.Type == TrackType.Subtitle)
                {
                    mf.AddTrack(new SubtitleTrack(file, new Info()));
                }
                else if (item.Type == TrackType.Chapter)
                {
                    mf.AddTrack(new ChapterTrack(file));
                }
                else if (item.Type == TrackType.Video)
                {
                    mf.AddTrack(new VideoTrack(file, new VideoInfo()));
                }
                else
                {
                    System.Windows.MessageBox.Show(item.OutFileName, "不认识的轨道呢");
                }
            }

            return mf;
        }
    }
}
