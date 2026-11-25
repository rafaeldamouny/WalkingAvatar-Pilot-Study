using UnityEngine;
using System.Collections.Generic;
using System.IO;
using System.Text;

public class GaitPredictor : MonoBehaviour
{
    [Header("References")]
    public Transform headTransform;

    [Header("Detection Settings")]
    public float walkingSpeedThreshold = 0.3f;
    public float sampleRate = 0.01f;
    public int bufferSize = 100;

    [Header("Logging")]
    public bool enableCSVLogging = true;
    public float logRate = 0.02f; // log every 20ms

    [Header("Debug")]
    public bool showDebugLogs = true;

    // Gait state
    public enum GaitPhase { Standing, LeftFootForward, RightFootForward }
    public GaitPhase currentPhase { get; private set; } = GaitPhase.Standing;
    public float strideFrequency { get; private set; } = 0f;
    public float confidence { get; private set; } = 0f;

    // Events
    public System.Action<GaitPhase> OnGaitPhaseChanged;
    public System.Action OnStepDetected;

    // Internal buffers
    private List<float> verticalBuffer = new List<float>();
    private List<float> lateralBuffer = new List<float>();
    private List<float> speedBuffer = new List<float>();
    private List<float> timeBuffer = new List<float>();

    private Vector3 lastHeadPos;
    private Vector3 headVelocity;
    private float sampleTimer = 0f;
    private GaitPhase previousPhase;

    // Vertical oscillation tracking
    private float lastVerticalPeak = 0f;
    private float lastVerticalValley = 0f;
    private bool lookingForPeak = true;
    private float lastPeakTime = 0f;
    private float lastValleyTime = 0f;

    // Step detection
    private int stepCount = 0;
    private float lastStepTime = 0f;

    // CSV logging
    private StringBuilder csvBuilder;
    private string csvFilePath;
    private float logTimer = 0f;
    private float sessionStartTime;
    private int logEntryCount = 0;
    private const int FLUSH_INTERVAL = 100; // flush every 100 entries

    void Start()
    {
        if (headTransform == null)
        {
            var cam = Camera.main;
            if (cam != null) headTransform = cam.transform;
        }

        lastHeadPos = headTransform.position;
        sessionStartTime = Time.time;

        if (enableCSVLogging)
        {
            InitCSV();
        }
    }

    void InitCSV()
    {
        string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        csvFilePath = Path.Combine(Application.persistentDataPath, $"gait_data_{timestamp}.csv");

        csvBuilder = new StringBuilder();
        csvBuilder.AppendLine(
            "timestamp," +
            "session_time," +
            "head_pos_x,head_pos_y,head_pos_z," +
            "head_vel_x,head_vel_y,head_vel_z," +
            "horizontal_speed," +
            "vertical_smooth," +
            "lateral_smooth," +
            "lateral_mean," +
            "gait_phase," +
            "stride_frequency," +
            "confidence," +
            "step_count," +
            "is_step_frame"
        );

        Debug.Log($"GAIT CSV: Logging to {csvFilePath}");
    }

    void Update()
    {
        if (headTransform == null) return;

        Vector3 currentPos = headTransform.position;
        headVelocity = (currentPos - lastHeadPos) / Time.deltaTime;
        float horizontalSpeed = new Vector2(headVelocity.x, headVelocity.z).magnitude;

        sampleTimer += Time.deltaTime;
        if (sampleTimer >= sampleRate)
        {
            sampleTimer = 0f;
            SampleHeadMotion(currentPos, horizontalSpeed);
        }

        // CSV logging at its own rate
        if (enableCSVLogging)
        {
            logTimer += Time.deltaTime;
            if (logTimer >= logRate)
            {
                logTimer = 0f;
                LogToCSV(currentPos, horizontalSpeed, false);
            }
        }

        lastHeadPos = currentPos;
    }

    void SampleHeadMotion(Vector3 headPos, float speed)
    {
        Vector3 forward = headTransform.forward;
        forward.y = 0;
        forward.Normalize();
        Vector3 right = new Vector3(forward.z, 0, -forward.x);

        float verticalPos = headPos.y;
        float lateralPos = Vector3.Dot(headPos, right);

        verticalBuffer.Add(verticalPos);
        lateralBuffer.Add(lateralPos);
        speedBuffer.Add(speed);
        timeBuffer.Add(Time.time);

        while (verticalBuffer.Count > bufferSize)
        {
            verticalBuffer.RemoveAt(0);
            lateralBuffer.RemoveAt(0);
            speedBuffer.RemoveAt(0);
            timeBuffer.RemoveAt(0);
        }

        if (verticalBuffer.Count < 20) return;

        float avgSpeed = 0f;
        for (int i = speedBuffer.Count - 10; i < speedBuffer.Count; i++)
            avgSpeed += speedBuffer[i];
        avgSpeed /= 10f;

        if (avgSpeed < walkingSpeedThreshold)
        {
            SetPhase(GaitPhase.Standing);
            return;
        }

        DetectSteps(verticalPos, lateralPos);
    }

    void DetectSteps(float verticalPos, float lateralPos)
    {
        float smoothedVertical = 0f;
        int smoothWindow = Mathf.Min(5, verticalBuffer.Count);
        for (int i = verticalBuffer.Count - smoothWindow; i < verticalBuffer.Count; i++)
            smoothedVertical += verticalBuffer[i];
        smoothedVertical /= smoothWindow;

        float smoothedLateral = 0f;
        for (int i = lateralBuffer.Count - smoothWindow; i < lateralBuffer.Count; i++)
            smoothedLateral += lateralBuffer[i];
        smoothedLateral /= smoothWindow;

        if (lookingForPeak)
        {
            if (smoothedVertical > lastVerticalPeak)
            {
                lastVerticalPeak = smoothedVertical;
            }
            else if (smoothedVertical < lastVerticalPeak - 0.003f)
            {
                float timeSinceLastStep = Time.time - lastStepTime;

                if (timeSinceLastStep > 0.25f && timeSinceLastStep < 1.5f)
                {
                    stepCount++;
                    strideFrequency = 1f / timeSinceLastStep;

                    if (smoothedLateral > GetLateralMean())
                    {
                        SetPhase(GaitPhase.LeftFootForward);
                    }
                    else
                    {
                        SetPhase(GaitPhase.RightFootForward);
                    }

                    OnStepDetected?.Invoke();

                    // Log step event
                    if (enableCSVLogging)
                    {
                        LogToCSV(headTransform.position,
                            new Vector2(headVelocity.x, headVelocity.z).magnitude, true);
                    }

                    if (showDebugLogs)
                    {
                        Debug.Log($"GAIT: Step #{stepCount} | {currentPhase} | Freq: {strideFrequency:F1} Hz | Confidence: {confidence:F2}");
                    }
                }

                lastStepTime = Time.time;
                lastPeakTime = Time.time;
                lookingForPeak = false;
                lastVerticalValley = smoothedVertical;
            }
        }
        else
        {
            if (smoothedVertical < lastVerticalValley)
            {
                lastVerticalValley = smoothedVertical;
            }
            else if (smoothedVertical > lastVerticalValley + 0.003f)
            {
                lastValleyTime = Time.time;
                lookingForPeak = true;
                lastVerticalPeak = smoothedVertical;
            }
        }

        UpdateConfidence();
    }

    void LogToCSV(Vector3 headPos, float horizontalSpeed, bool isStepFrame)
    {
        if (csvBuilder == null) return;

        float smoothedVertical = GetSmoothedVertical();
        float smoothedLateral = GetSmoothedLateral();

        csvBuilder.AppendLine(
            $"{System.DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}," +
            $"{(Time.time - sessionStartTime):F4}," +
            $"{headPos.x:F6},{headPos.y:F6},{headPos.z:F6}," +
            $"{headVelocity.x:F6},{headVelocity.y:F6},{headVelocity.z:F6}," +
            $"{horizontalSpeed:F6}," +
            $"{smoothedVertical:F6}," +
            $"{smoothedLateral:F6}," +
            $"{GetLateralMean():F6}," +
            $"{currentPhase}," +
            $"{strideFrequency:F4}," +
            $"{confidence:F4}," +
            $"{stepCount}," +
            $"{(isStepFrame ? 1 : 0)}"
        );

        logEntryCount++;
        if (logEntryCount % FLUSH_INTERVAL == 0)
        {
            FlushCSV();
        }
    }

    float GetSmoothedVertical()
    {
        if (verticalBuffer.Count == 0) return 0f;
        float sum = 0f;
        int window = Mathf.Min(5, verticalBuffer.Count);
        for (int i = verticalBuffer.Count - window; i < verticalBuffer.Count; i++)
            sum += verticalBuffer[i];
        return sum / window;
    }

    float GetSmoothedLateral()
    {
        if (lateralBuffer.Count == 0) return 0f;
        float sum = 0f;
        int window = Mathf.Min(5, lateralBuffer.Count);
        for (int i = lateralBuffer.Count - window; i < lateralBuffer.Count; i++)
            sum += lateralBuffer[i];
        return sum / window;
    }

    float GetLateralMean()
    {
        if (lateralBuffer.Count == 0) return 0f;
        float sum = 0f;
        int count = Mathf.Min(50, lateralBuffer.Count);
        for (int i = lateralBuffer.Count - count; i < lateralBuffer.Count; i++)
            sum += lateralBuffer[i];
        return sum / count;
    }

    float GetLateralVelocity()
    {
        if (lateralBuffer.Count < 3) return 0f;
        return (lateralBuffer[lateralBuffer.Count - 1] - lateralBuffer[lateralBuffer.Count - 3]) / (sampleRate * 2);
    }

    void UpdateConfidence()
    {
        float freqConfidence = 0f;
        if (strideFrequency > 0.8f && strideFrequency < 2.5f)
            freqConfidence = 1f;
        else if (strideFrequency > 0.5f && strideFrequency < 3f)
            freqConfidence = 0.5f;

        float recentStepsConfidence = Mathf.Clamp01(stepCount / 5f);

        float timeSinceStep = Time.time - lastStepTime;
        float recencyConfidence = timeSinceStep < 1f ? 1f : Mathf.Clamp01(2f - timeSinceStep);

        confidence = (freqConfidence + recentStepsConfidence + recencyConfidence) / 3f;
    }

    void SetPhase(GaitPhase newPhase)
    {
        previousPhase = currentPhase;
        currentPhase = newPhase;

        if (currentPhase != previousPhase)
        {
            OnGaitPhaseChanged?.Invoke(currentPhase);
        }
    }

    void FlushCSV()
    {
        if (csvBuilder == null || csvBuilder.Length == 0) return;

        try
        {
            File.AppendAllText(csvFilePath, csvBuilder.ToString());
            csvBuilder.Clear();
        }
        catch (System.Exception e)
        {
            Debug.LogError($"GAIT CSV: Failed to write - {e.Message}");
        }
    }

    void OnApplicationPause(bool paused)
    {
        if (paused && enableCSVLogging)
        {
            FlushCSV();
            Debug.Log($"GAIT CSV: Flushed on pause. Total entries: {logEntryCount}");
        }
    }

    void OnDestroy()
    {
        if (enableCSVLogging)
        {
            FlushCSV();
            Debug.Log($"GAIT CSV: Final flush. Total entries: {logEntryCount}. File: {csvFilePath}");
        }
    }

    // Public API
    public bool IsWalking() => currentPhase != GaitPhase.Standing;
    public string GetCurrentLeg()
    {
        switch (currentPhase)
        {
            case GaitPhase.LeftFootForward: return "Left";
            case GaitPhase.RightFootForward: return "Right";
            default: return "None";
        }
    }

    public string GetCSVPath() => csvFilePath;

    public void ResetTracking()
    {
        verticalBuffer.Clear();
        lateralBuffer.Clear();
        speedBuffer.Clear();
        timeBuffer.Clear();
        stepCount = 0;
        currentPhase = GaitPhase.Standing;
        confidence = 0f;
    }
}