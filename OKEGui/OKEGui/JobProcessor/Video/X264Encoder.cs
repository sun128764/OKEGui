﻿using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using OKEGui.Utils;
using OKEGui.JobProcessor;

namespace OKEGui
{
    public class X264Encoder : CommandlineVideoEncoder
    {
        private readonly string X264Path = "";
        private readonly string VspipePath = "";

        public X264Encoder(Job j) : base()
        {
            job = j as VideoJob;
            getInputProperties(job);

            executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");

            if (File.Exists(job.EncoderPath))
            {
                this.X264Path = job.EncoderPath;
            }

            // 获取VSPipe路径
            this.VspipePath = ConfigManager.Config.vspipePath;

            commandLine = BuildCommandline(job.EncodeParam, job.NumaNode);
        }

        public override void ProcessLine(string line, StreamType stream)
        {
            if (line.Contains("x264 [error]:"))
            {
                OKETaskException ex = new OKETaskException(Constants.x264ErrorSmr);
                ex.progress = 0.0;
                ex.Data["X264_ERROR"] = line.Substring(14);
                throw ex;
            }

            if (line.Contains("Error: fwrite() call failed when writing frame: "))
            {
                OKETaskException ex = new OKETaskException(Constants.x264CrashSmr);
                throw ex;
            }

            if (line.ToLowerInvariant().Contains("encoded"))
            {
                Regex rf = new Regex("encoded ([0-9]+) frames, ([0-9]+.[0-9]+) fps, ([0-9]+.[0-9]+) kb/s");

                var result = rf.Split(line);

                ulong reportedFrames = ulong.Parse(result[1]);

                // 这里是平均速度
                if (!base.setSpeed(result[2]))
                {
                    return;
                }

                Debugger.Log(0, "EncodeFinish", result[2] + "fps\n");

                base.encodeFinish(reportedFrames);
            }

            Regex r = new Regex("([0-9]+) frames: ([0-9]+.[0-9]+) fps, ([0-9]+.[0-9]+) kb/s", RegexOptions.IgnoreCase);

            var status = r.Split(line);
            if (status.Length < 3)
            {
                return;
            }

            if (!base.setFrameNumber(status[1], true))
            {
                return;
            }

            base.setBitrate(status[3], "kb/s");

            if (!base.setSpeed(status[2]))
            {
                return;
            }

            base.ProcessLine(line, stream);
        }

        private string BuildCommandline(string extractParam, int numaNode)
        {
            StringBuilder sb = new StringBuilder();

            sb.Append("/c \"start \"foo\" /b /wait /affinity 0xFFFFFFF /node ");
            sb.Append(numaNode.ToString());
            // 构建vspipe参数
            sb.Append(" \"" + VspipePath + "\"");
            sb.Append(" --y4m ");
            sb.Append("\"" + job.Input + "\"");
            sb.Append(" - | ");

            // 构建X264参数
            sb.Append("\"" + X264Path + "\"");
            sb.Append(" --demuxer y4m " + extractParam + " -o ");
            sb.Append("\"" + job.Output + "\" -");
            sb.Append("\"");

            return sb.ToString();
        }

    }
}