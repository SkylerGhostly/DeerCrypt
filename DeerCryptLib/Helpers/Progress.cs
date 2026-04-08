using System;
using System.Collections.Generic;
using System.Text;

namespace DeerCryptLib.Helpers
{
    public class FileProgress( string name, double progress )
    {
        public string Name { get; set; } = name;
        public double Progress { get; set; } = progress;
    }

    public class BatchProgress( int filesDone, int fileCount, double progress )
    {
        public int FilesDone { get; set; } = filesDone;
        public int FileCount { get; set; } = fileCount;
        public double Progress { get; set; } = progress;
    }
}
