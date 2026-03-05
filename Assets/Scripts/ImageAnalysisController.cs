using Meta.XR;
using Meta.XR.BuildingBlocks.AIBlocks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using System.Collections;

public class ImageAnalysisController : MonoBehaviour
{
    [SerializeField] private LlmAgent llmAgent;
    [SerializeField] private PassthroughCameraAccess passthroughCameraAccess;

    [Header("UI")]
    [SerializeField] private Button STTButton;
    [SerializeField] private QuestTMPKeyboard promptKeyboardText;
    [SerializeField] private TMP_InputField promptInputField;

    [Header("AI")]
    [SerializeField] private SpeechToTextAgent sstAgent;
    [SerializeField] private TextToSpeechAgent ttsAgent;

    private Texture2D capturedFrame;
    private bool isListening = false;
    private bool micWarmedUp = false;
    private TextMeshProUGUI sttButtonText;

    // Blit throttle (kept minimal from your previous code)
    private RenderTexture renderTexture;
    private float lastBlitTime = 0f;
    private float blitInterval = 1f / 45f;

    private void Awake()
    {
        // cache button text
        sttButtonText = STTButton.GetComponentInChildren<TextMeshProUGUI>();
        if (sttButtonText != null) sttButtonText.text = "Start Mic";

        // STT transcript listener: ensure no duplicate listeners
        if (sstAgent != null)
        {
            sstAgent.onTranscript.RemoveListener(OnTranscriptReceived);
            sstAgent.onTranscript.AddListener(OnTranscriptReceived);
        }

        // LLM -> TTS listener (single registration)
        if (llmAgent != null)
        {
            llmAgent.onResponseReceived.RemoveAllListeners();
            llmAgent.onResponseReceived.AddListener(OnLlmResponseReceived);
        }

        // TTS listeners (optional logging)
        if (ttsAgent != null)
        {
            ttsAgent.onClipReady.RemoveAllListeners();
            ttsAgent.onSpeakStarting.RemoveAllListeners();
            ttsAgent.onSpeakFinished.RemoveAllListeners();

            ttsAgent.onClipReady.AddListener(clip => Debug.Log("[TTS] onClipReady"));
            ttsAgent.onSpeakStarting.AddListener(clip => Debug.Log("[TTS] onSpeakStarting"));
            ttsAgent.onSpeakFinished.AddListener(() => Debug.Log("[TTS] onSpeakFinished"));
        }

        // STT button wiring (start/stop)
        STTButton.onClick.RemoveAllListeners();
        STTButton.onClick.AddListener(() =>
        {
            if (!isListening)
                StartCoroutine(StartSTTAsync());
            else
                StopSTT();
        });

        // setup passthrough render texture (kept from your previous script so capture works)
        renderTexture = new RenderTexture(1024, 1024, 0);
        renderTexture.Create();

        if (passthroughCameraAccess != null && passthroughCameraAccess.TargetMaterial != null)
        {
            Graphics.Blit(null, renderTexture, passthroughCameraAccess.TargetMaterial);
        }
    }

    private void Start()
    {
        // warm up mic once to avoid visible hitch when user first presses mic
        StartCoroutine(WarmupMic());
    }

    private IEnumerator WarmupMic()
    {
        if (sstAgent == null || micWarmedUp) yield break;

        yield return new WaitForSecondsRealtime(0.5f);

        bool started = false;
        try
        {
            sstAgent.StartListening();
            started = true;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("[STT] Mic warmup start failed: " + ex.Message);
        }

        if (!started) yield break;

        yield return new WaitForSecondsRealtime(0.15f);

        try
        {
            sstAgent.StopNow();
            micWarmedUp = true;
            Debug.Log("[STT] Mic warmup complete.");
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("[STT] Mic warmup stop failed: " + ex.Message);
        }
    }

    private void Update()
    {
        // blit passthrough when not listening (keeps GPU and audio init separated)
        if (passthroughCameraAccess != null && passthroughCameraAccess.TargetMaterial != null && !isListening)
        {
            if (Time.realtimeSinceStartup - lastBlitTime >= blitInterval)
            {
                Graphics.Blit(null, renderTexture, passthroughCameraAccess.TargetMaterial);
                lastBlitTime = Time.realtimeSinceStartup;
            }
        }
    }

    private IEnumerator StartSTTAsync()
    {
        if (sstAgent == null)
        {
            Debug.LogWarning("[STT] SpeechToTextAgent is null.");
            yield break;
        }

        // finish the click frame so UI doesn't hitch
        yield return null;
        yield return new WaitForSecondsRealtime(0.02f);

        try
        {
            Debug.Log("[STT] StartListening()");
            isListening = true;
            if (sttButtonText != null) sttButtonText.text = "Stop Mic";

            sstAgent.StartListening();
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("[STT] StartListening failed: " + ex.Message);
            isListening = false;
            if (sttButtonText != null) sttButtonText.text = "Start Mic";
        }
    }

    private void StopSTT()
    {
        if (sstAgent == null)
        {
            Debug.LogWarning("[STT] SpeechToTextAgent is null.");
            return;
        }

        try
        {
            Debug.Log("[STT] StopNow()");
            sstAgent.StopNow();
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("[STT] StopNow failed: " + ex.Message);
        }
        finally
        {
            isListening = false;
            if (sttButtonText != null) sttButtonText.text = "Start Mic";
        }

        // IMPORTANT: wait briefly or poll LastTranscript so we don't send before the final transcript arrives.
        StartCoroutine(WaitForTranscriptThenSend(0.7f)); // 700ms max wait
    }

    /// <summary>
    /// Poll for final transcript (InputField or sstAgent.LastTranscript) for up to timeout seconds.
    /// Then capture camera and send to LLM.
    /// </summary>
    private IEnumerator WaitForTranscriptThenSend(float timeoutSeconds)
    {
        float start = Time.realtimeSinceStartup;
        string transcript = string.Empty;

        while (Time.realtimeSinceStartup - start < timeoutSeconds)
        {
            // prefer promptInputField (it's updated by OnTranscriptReceived), otherwise fallback to sstAgent.LastTranscript if available
            if (promptInputField != null && !string.IsNullOrWhiteSpace(promptInputField.text))
            {
                transcript = promptInputField.text.Trim();
                break;
            }

            // fallback if the agent exposes LastTranscript (use reflection-safe check)
            if (sstAgent != null)
            {
                // many provider implementations expose LastTranscript; try to read it safely
                try
                {
                    var last = sstAgent.GetType().GetProperty("LastTranscript")?.GetValue(sstAgent) as string;
                    if (!string.IsNullOrWhiteSpace(last))
                    {
                        transcript = last.Trim();
                        break;
                    }
                }
                catch { /* ignore reflection errors */ }
            }

            // small wait then continue polling
            yield return new WaitForSecondsRealtime(0.05f);
        }

        // If still empty, take whatever is in the input field (may be empty)
        if (string.IsNullOrWhiteSpace(transcript) && promptInputField != null)
            transcript = promptInputField.text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(transcript))
            Debug.LogWarning("[STT] No transcript captured before sending to LLM (sending empty prompt).");

        // capture frame and send
        yield return CaptureFrameAndSendAsync(transcript);
    }

    /// <summary>
    /// Captures the passthrough camera texture into a Texture2D and sends prompt+image to the LLM.
    /// </summary>
    private IEnumerator CaptureFrameAndSendAsync(string prompt)
    {
        if (passthroughCameraAccess == null)
        {
            Debug.LogWarning("[Capture] PassthroughCameraAccess null - sending prompt only.");
            _ = llmAgent.SendPromptAsync(prompt, null);
            yield break;
        }

        var sourceTexture = passthroughCameraAccess.GetTexture();
        if (sourceTexture == null)
        {
            Debug.LogWarning("[Capture] Passthrough camera texture not ready - sending prompt only.");
            _ = llmAgent.SendPromptAsync(prompt, null);
            yield break;
        }

        // ensure capturedFrame size matches
        if (capturedFrame == null || capturedFrame.width != sourceTexture.width || capturedFrame.height != sourceTexture.height)
        {
            if (capturedFrame != null) Destroy(capturedFrame);
            capturedFrame = new Texture2D(sourceTexture.width, sourceTexture.height, TextureFormat.RGBA32, false);
        }

        // copy using temporary RT
        RenderTexture currentRT = RenderTexture.active;
        RenderTexture tempRT = RenderTexture.GetTemporary(sourceTexture.width, sourceTexture.height, 0, RenderTextureFormat.ARGB32);

        Graphics.Blit(sourceTexture, tempRT);
        RenderTexture.active = tempRT;

        capturedFrame.ReadPixels(new Rect(0, 0, tempRT.width, tempRT.height), 0, 0);
        capturedFrame.Apply();

        RenderTexture.active = currentRT;
        RenderTexture.ReleaseTemporary(tempRT);

        Debug.Log($"[Capture] Frame captured: {capturedFrame.width}x{capturedFrame.height}. Sending prompt: \"{(string.IsNullOrEmpty(prompt) ? "<EMPTY>" : prompt)}\"");

        // send to LLM (fire-and-forget)
        _ = llmAgent.SendPromptAsync(prompt, capturedFrame);

        // small yield so caller doesn't block
        yield return null;
    }

    private void OnTranscriptReceived(string transcript)
    {
        Debug.Log("[STT] Transcript received: " + transcript);

        if (promptInputField != null)
        {
            promptInputField.text = transcript;
        }
    }

    private void OnLlmResponseReceived(string response)
    {
        Debug.Log("[LLM] Response received (len=" + (response?.Length ?? 0) + "): " + (string.IsNullOrEmpty(response) ? "<EMPTY>" : response));

        // Don't speak empty responses — avoids playing TTS inspector default text.
        if (string.IsNullOrWhiteSpace(response))
        {
            Debug.LogWarning("[LLM] Empty response — skipping TTS.");
        }
        else
        {
            if (ttsAgent != null)
            {
                ttsAgent.SpeakText(response);
            }
        }

        // Reset UI for next question
        ResetState();
    }

    private void ResetState()
    {
        // clear the input field (so next mic session starts fresh)
        if (promptInputField != null)
            promptInputField.text = string.Empty;

        // ensure button text is correct (already set on stop but keep consistent)
        if (sttButtonText != null)
            sttButtonText.text = "Start Mic";
    }

    private void OnDestroy()
    {
        if (sstAgent != null)
            sstAgent.onTranscript.RemoveListener(OnTranscriptReceived);

        if (llmAgent != null)
            llmAgent.onResponseReceived.RemoveListener(OnLlmResponseReceived);

        if (ttsAgent != null)
        {
            ttsAgent.onClipReady.RemoveAllListeners();
            ttsAgent.onSpeakStarting.RemoveAllListeners();
            ttsAgent.onSpeakFinished.RemoveAllListeners();
        }

        if (capturedFrame != null)
        {
            Destroy(capturedFrame);
            capturedFrame = null;
        }

        if (renderTexture != null)
        {
            renderTexture.Release();
            renderTexture = null;
        }
    }
}