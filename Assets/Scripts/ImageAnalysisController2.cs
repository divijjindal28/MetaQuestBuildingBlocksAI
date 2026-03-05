using Meta.XR;
using Meta.XR.BuildingBlocks.AIBlocks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using Toggle = UnityEngine.UI.Toggle;

public class ImageAnalysisController2 : MonoBehaviour
{
    [SerializeField] private LlmAgent llmAgent;
    [SerializeField] private PassthroughCameraAccess passthroughCameraAccess;

    [Header("UI Bindings")]
    //[SerializeField] private RawImage liveImage;
    //[SerializeField] private RawImage capturedImage;
    [SerializeField] private Toggle captureButton;
    [SerializeField] private Button STTButton;
    [SerializeField] private QuestTMPKeyboard promptKeyboardText;
    //[SerializeField] private RectTransform llmResponseScrollView;

    [Header("TTS & STT Bindings")]
    [SerializeField] private TextToSpeechAgent ttsAgent;
    [SerializeField] private SpeechToTextAgent sstAgent;

    [Header("Input")]
    [SerializeField] private TMP_InputField promptInputField;

    private TextMeshProUGUI capturedText;
    private TextMeshProUGUI llmResponseText;
    private RenderTexture renderTexture;
    private Texture2D capturedFrame;
    private bool capturingInProgress;

    private bool isListening = false;
    private bool micWarmedUp = false;
    private TextMeshProUGUI sttButtonText;

    // Throttle passthrough blit to avoid accidentally fighting STT init
    private float lastBlitTime = 0f;
    private float blitInterval = 1f / 45f; // target ~45 fps for UI blit, change if needed

    private bool CapturingInProgress
    {
        get => capturingInProgress;
        set
        {
            capturingInProgress = value;
            //liveImage.gameObject.SetActive(!value);
            //capturedImage.gameObject.SetActive(value);
            if (capturedText != null) capturedText.text = value ? "Reset" : "Ask";
            //if (llmResponseScrollView != null) llmResponseScrollView.gameObject.SetActive(value);
        }
    }

    private void Awake()
    {
        if (passthroughCameraAccess == null)
        {
            Debug.LogError("[PassthroughCameraAddOns] PassthroughCameraAccess component not found!");
            return;
        }

        // Cache UI references
        sttButtonText = STTButton.GetComponentInChildren<TextMeshProUGUI>();
        if (sttButtonText != null) sttButtonText.text = "Start Mic";

        capturedText = captureButton.GetComponentInChildren<TextMeshProUGUI>();
        //llmResponseText = llmResponseScrollView.GetComponentInChildren<TextMeshProUGUI>();
        //if (llmResponseText != null) llmResponseText.text = string.Empty;

        CapturingInProgress = false;

        // render texture for live passthrough
        renderTexture = new RenderTexture(1024, 1024, 0);
        renderTexture.Create();

        if (passthroughCameraAccess.TargetMaterial != null)
        {
            Graphics.Blit(null, renderTexture, passthroughCameraAccess.TargetMaterial);
            //liveImage.texture = renderTexture;
        }

        // STT button wiring (start/stop)
        STTButton.onClick.AddListener(() =>
        {
            if (!isListening)
                StartCoroutine(StartSTTAsync());
            else
                StopSTT();
        });

        // Capture toggle
        captureButton.onValueChanged.AddListener((_) =>
        {
            if (CapturingInProgress)
            {
                CapturingInProgress = false;
            }
            else
            {
                CapturingInProgress = true;
                CaptureFrame();
            }
        });

        // LLM response handling (only speak; listeners registered once below)
        if (llmAgent != null)
        {
            llmAgent.onResponseReceived.AddListener(response =>
            {
                //if (llmResponseText != null) llmResponseText.text = response;
                Debug.Log("Response received: " + response);

                if (ttsAgent != null)
                {
                    // Speak (listeners already registered once in Awake/Start)
                    ttsAgent.SpeakText(response);
                }
            });
        }

        // Register TTS listeners once (avoid adding repeatedly)
        if (ttsAgent != null)
        {
            ttsAgent.onClipReady.RemoveAllListeners();
            ttsAgent.onSpeakStarting.RemoveAllListeners();
            ttsAgent.onSpeakFinished.RemoveAllListeners();

            ttsAgent.onClipReady.AddListener(clip => { Debug.Log("[TTS] onClipReady"); });
            ttsAgent.onSpeakStarting.AddListener(clip => { Debug.Log("[TTS] onSpeakStarting"); });
            ttsAgent.onSpeakFinished.AddListener(() => { Debug.Log("[TTS] onSpeakFinished"); });
        }

        // STT transcript listener: ensure no duplicate listeners
        if (sstAgent != null)
        {
            sstAgent.onTranscript.RemoveListener(OnTranscriptReceived);
            sstAgent.onTranscript.AddListener(OnTranscriptReceived);
        }
    }

    private void Start()
    {
        // Warm up mic in background shortly after startup to avoid user-visible hitch later
        StartCoroutine(WarmupMic());
    }

    private IEnumerator WarmupMic()
    {
        if (sstAgent == null) yield break;
        if (micWarmedUp) yield break;

        yield return new WaitForSeconds(0.5f);

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

        yield return new WaitForSeconds(0.15f);

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

    void Update()
    {
        // Only blit passthrough when not capturing and not listening.
        // This prevents GPU work from fighting the audio initialization when user starts mic.
        if (passthroughCameraAccess.TargetMaterial != null && !CapturingInProgress && !isListening)
        {
            if (Time.realtimeSinceStartup - lastBlitTime >= blitInterval)
            {
                Graphics.Blit(null, renderTexture, passthroughCameraAccess.TargetMaterial);
                lastBlitTime = Time.realtimeSinceStartup;
            }
        }
    }

    // Start STT on the next frame to avoid hitching the button click frame
    private IEnumerator StartSTTAsync()
    {
        if (sstAgent == null)
        {
            Debug.LogWarning("[STT] SpeechToTextAgent is null.");
            yield break;
        }

        // Let the click frame finish
        yield return null;
        yield return new WaitForSeconds(0.02f); // small buffer so UI updates complete

        try
        {
            Debug.Log("[STT] StartListening()");
            isListening = true;

            // stop passthrough blit immediately (Update respects isListening)
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
    }

    private void OnTranscriptReceived(string transcript)
    {
        Debug.Log("[STT] Transcript: " + transcript);

        if (promptInputField != null)
        {
            // replace with transcript (or append if you prefer)
            promptInputField.text = transcript;
        }
    }

    private void CaptureFrame()
    {
        if (passthroughCameraAccess == null)
        {
            Debug.LogError("[PassthroughCameraAddOns] PassthroughCameraAccess is null. Cannot capture frame.");
            return;
        }

        var sourceTexture = passthroughCameraAccess.GetTexture();
        if (sourceTexture == null)
        {
            Debug.LogWarning("[PassthroughCameraAddOns] Passthrough camera texture is not available yet.");
            return;
        }

        // Create or resize the Texture2D if needed
        if (capturedFrame == null ||
            capturedFrame.width != sourceTexture.width ||
            capturedFrame.height != sourceTexture.height)
        {
            if (capturedFrame != null)
            {
                Destroy(capturedFrame);
            }
            capturedFrame = new Texture2D(sourceTexture.width, sourceTexture.height, TextureFormat.RGBA32, false);
        }

        // Copy the texture data using a temporary RenderTexture
        RenderTexture currentRT = RenderTexture.active;
        RenderTexture tempRT = RenderTexture.GetTemporary(sourceTexture.width, sourceTexture.height, 0, RenderTextureFormat.ARGB32);

        Graphics.Blit(sourceTexture, tempRT);
        RenderTexture.active = tempRT;

        capturedFrame.ReadPixels(new Rect(0, 0, tempRT.width, tempRT.height), 0, 0);
        capturedFrame.Apply();

        RenderTexture.active = currentRT;
        RenderTexture.ReleaseTemporary(tempRT);

        if (capturedFrame != null)
        {
            Debug.Log($"[PassthroughCameraAddOns] Frame captured: {capturedFrame.width}x{capturedFrame.height}");
            //capturedImage.texture = capturedFrame;

            // Use the actual input field text (not QuestTMPKeyboard.KeyboardText which may be read-only)
            string promptText = promptInputField != null ? promptInputField.text : (promptKeyboardText != null ? promptKeyboardText.KeyboardText : string.Empty);

            _ = llmAgent.SendPromptAsync(promptText, capturedFrame);
        }
    }

    private void OnDestroy()
    {
        // remove listeners
        if (llmAgent != null)
            llmAgent.onResponseReceived.RemoveAllListeners();

        if (ttsAgent != null)
        {
            ttsAgent.onClipReady.RemoveAllListeners();
            ttsAgent.onSpeakStarting.RemoveAllListeners();
            ttsAgent.onSpeakFinished.RemoveAllListeners();
        }

        if (sstAgent != null)
            sstAgent.onTranscript.RemoveListener(OnTranscriptReceived);

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