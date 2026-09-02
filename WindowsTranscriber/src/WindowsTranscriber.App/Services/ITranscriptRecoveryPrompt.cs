using WindowsTranscriber.Core.Models;

namespace WindowsTranscriber.App.Services;

public interface ITranscriptRecoveryPrompt
{
    bool ShouldRestore(TranscriptRecoverySession session);
}
