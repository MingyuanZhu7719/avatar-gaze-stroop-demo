using System;

namespace AvatarStroop.Runtime
{
    // Excel can canonicalize an 8.3 alias back to the full path. Keep the
    // original output path within its supported budget; never copy data or
    // treat an alias as a workaround for an already-too-long recording.
    internal static class ViveCsvPathPolicy
    {
        internal const int MaximumApplicationPathCharacters=259;
        internal static string StageFileName(Guid identity)
        {return "vstage-"+identity.ToString("N")+".partial.csv";}
        internal static bool FitsSessionDirectory(string directory)
        {return !String.IsNullOrEmpty(directory)&&directory.TrimEnd('\\').Length<=
            MaximumApplicationPathCharacters-1-StageFileName(Guid.Empty).Length;}
        internal static bool FitsExistingFile(string path)
        {return !String.IsNullOrEmpty(path)&&path.Length<=MaximumApplicationPathCharacters;}
        internal const string PreparationNotice="Choose a shorter session folder before recording: the complete CSV path must fit within 259 characters for Open CSV.";
        internal const string OpenNotice="CSV is saved, but its full path is too long for the spreadsheet application. Use the data analyzer, or a shorter folder. The saved file was not changed.";
    }
}
