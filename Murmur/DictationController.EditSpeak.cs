namespace Murmur;

/// <summary>Voice editing and read aloud (edit-speak): the controller's side (VoiceEditField.cs, ReadAloud.cs, EditSpeakIntents.cs).</summary>
public sealed partial class DictationController
{
    private void InitEditSpeak()
    {
        // The card's reading state, its keycaps and Esc; the intents find it as ReadAloud.Current.
        ReadAloud.Attach(_overlay, _settings);
        // A voice edit changes the field on purpose: that is not a correction for the learner to pick up.
        VoiceEditor.BeforeChange = () => _assist.StopWatching();
    }

    /// <summary>Text Murmur typed into an app: "gør det mere høfligt" with nothing selected is about it.</summary>
    private static void NoteTypedForEditing(string text, IntPtr window) => VoiceEditor.NoteTyped(text, window);
}
