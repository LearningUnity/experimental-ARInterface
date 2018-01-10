﻿using System;
using System.Collections.Generic;
using UnityEngine;
using GoogleARCore;
using GoogleARCoreInternal;
using System.Collections;

namespace UnityARInterface
{
    public class ARCoreInterface : ARInterface
    {
        private List<TrackedPlane> m_TrackedPlaneBuffer = new List<TrackedPlane>();
        private float? m_HorizontalFov;
        private float? m_VerticalFov;
        private ScreenOrientation m_CachedScreenOrientation;
        private Dictionary<TrackedPlane, BoundedPlane> m_TrackedPlanes = new Dictionary<TrackedPlane, BoundedPlane>();
        private SessionManager m_SessionManager;
        private ARCoreSessionConfig m_ARCoreSessionConfig;
        private ARCoreBackgroundRenderer m_BackgroundRenderer;
        private Matrix4x4 m_DisplayTransform = Matrix4x4.identity;
        private List<Vector4> m_TempPointCloud = new List<Vector4>();

        public override IEnumerator StartService(Settings settings)
        {
            if (m_ARCoreSessionConfig == null)
                m_ARCoreSessionConfig = ScriptableObject.CreateInstance<ARCoreSessionConfig>();

            m_ARCoreSessionConfig.EnableLightEstimation = settings.enableLightEstimation;
            m_ARCoreSessionConfig.EnablePlaneFinding = settings.enablePlaneDetection;
            //Do we want to match framerate to the camera?
            m_ARCoreSessionConfig.MatchCameraFramerate = false;

            //Using the SessionManager instead of ARCoreSession allows us to check if the config is supported,
            //And also using the session without the need for a GameObject or an additional MonoBehaviour.
            if (m_SessionManager == null)
            {
                m_SessionManager = SessionManager.CreateSession();
                if (!m_SessionManager.CheckSupported((m_ARCoreSessionConfig))){
                    ARDebug.LogError("The requested ARCore session configuration is not supported.");
                    yield break;
                }

                Session.Initialize(m_SessionManager);

                if (Session.ConnectionState != SessionConnectionState.Uninitialized)
                {
                    ARDebug.LogError("Could not create an ARCore session.  The current Unity Editor may not support this " +
                        "version of ARCore.");
                    yield break;
                }
            }
            //We ask for permission to use the camera and wait
            var task = AskForPermissionAndConnect(m_ARCoreSessionConfig);
            yield return task.WaitForCompletion();
            //After the operation is done, we double check if the connection was successful
            IsRunning = task.Result == SessionConnectionState.Connected;
        }

        //Checks if we can establish a connection, and ask for permission
        private AsyncTask<SessionConnectionState> AskForPermissionAndConnect(ARCoreSessionConfig sessionConfig)
        {
            const string androidCameraPermissionName = "android.permission.CAMERA";

            if (m_SessionManager == null)
            {
                ARDebug.LogError("Cannot connect because ARCoreSession failed to initialize.");
                return new AsyncTask<SessionConnectionState>(SessionConnectionState.Uninitialized);
            }

            if (sessionConfig == null)
            {
                ARDebug.LogError("Unable to connect ARSession session due to missing ARSessionConfig.");
                m_SessionManager.ConnectionState = SessionConnectionState.MissingConfiguration;
                return new AsyncTask<SessionConnectionState>(Session.ConnectionState);
            }

            // We have already connected at least once.
            if (Session.ConnectionState != SessionConnectionState.Uninitialized)
            {
                ARDebug.LogError("Multiple attempts to connect to the ARSession.  Note that the ARSession connection " +
                    "spans the lifetime of the application and cannot be reconfigured.  This will change in future " +
                    "versions of ARCore.");
                return new AsyncTask<SessionConnectionState>(Session.ConnectionState);
            }

            // Create an asynchronous task for the potential permissions flow and service connection.
            Action<SessionConnectionState> onTaskComplete;
            var returnTask = new AsyncTask<SessionConnectionState>(out onTaskComplete);
            returnTask.ThenAction((connectionState) =>
            {
                m_SessionManager.ConnectionState = connectionState;
            });

            // Attempt service connection immediately if permissions are granted.
            if (AndroidPermissionsManager.IsPermissionGranted(androidCameraPermissionName))
            {
                Connect(sessionConfig, onTaskComplete);
                return returnTask;
            }

            // Request needed permissions and attempt service connection if granted.
            AndroidPermissionsManager.RequestPermission(androidCameraPermissionName).ThenAction((requestResult) =>
            {
                if (requestResult.IsAllGranted)
                {
                    Connect(sessionConfig, onTaskComplete);
                }
                else
                {
                    ARDebug.LogError("ARCore connection failed because a needed permission was rejected.");
                    onTaskComplete(SessionConnectionState.UserRejectedNeededPermission);
                }
            });

            return returnTask;
        }

        //Connect is called once the permission to use the camera is granted.
        private void Connect(ARCoreSessionConfig sessionConfig, Action<SessionConnectionState> onComplete)
        {
            if (!m_SessionManager.CheckSupported(sessionConfig))
            {
                ARDebug.LogError("The requested ARCore session configuration is not supported.");
                onComplete(SessionConnectionState.InvalidConfiguration);
                return;
            }

            if (!m_SessionManager.SetConfiguration(sessionConfig))
            {
                ARDebug.LogError("ARCore connection failed because the current configuration is not supported.");
                onComplete(SessionConnectionState.InvalidConfiguration);
                return;
            }

            Frame.Initialize(m_SessionManager.FrameManager);

            // ArSession_resume needs to be called in the UI thread due to b/69682628.
            AsyncTask.PerformActionInUIThread(() =>
            {
                if (!m_SessionManager.Resume())
                {
                    onComplete(SessionConnectionState.ConnectToServiceFailed);
                }
                else
                {
                    onComplete(SessionConnectionState.Connected);
                }
            });
        }

        public override void StopService()
        {
            Frame.Destroy();
            Session.Destroy();
            IsRunning = false;
        }

        public override bool TryGetUnscaledPose(ref Pose pose)
        {
            if (Frame.TrackingState != TrackingState.Tracking)
                return false;

            pose.position = Frame.Pose.position;
            pose.rotation = Frame.Pose.rotation;
            return true;
        }

        public override bool TryGetCameraImage(ref CameraImage cameraImage)
        {
            if (Frame.TrackingState != TrackingState.Tracking)
                return false;

            var camTexture = Frame.CameraImage.Texture;
            cameraImage.height = camTexture.height;
            cameraImage.width = camTexture.width;
            return true;
        }

        public override bool TryGetPointCloud(ref PointCloud pointCloud)
        {
            // Fill in the data to draw the point cloud.
            m_TempPointCloud.Clear();
            Frame.PointCloud.CopyPoints(m_TempPointCloud);

            if (m_TempPointCloud.Count == 0)
                return false;

            if (pointCloud.points == null)
                pointCloud.points = new List<Vector3>();

            pointCloud.points.Clear();
            foreach (Vector3 point in m_TempPointCloud)
                pointCloud.points.Add(point);

            return true;
        }

        public override LightEstimate GetLightEstimate()
        {
            if (Session.ConnectionState == SessionConnectionState.Connected)
            {
                return new LightEstimate()
                {
                    capabilities = LightEstimateCapabilities.AmbientIntensity,
                    ambientIntensity = Frame.LightEstimate.PixelIntensity
                };
            }
            else
            {
                // Zero initialized means capabilities will be None
                return new LightEstimate();
            }
        }

		public override Matrix4x4 GetDisplayTransform()
		{
			return m_DisplayTransform;
		}

        private void CalculateDisplayTransform()
        {
            var cosTheta = 1f;
            var sinTheta = 0f;

            switch (Screen.orientation)
            {
                case ScreenOrientation.Portrait:
                    cosTheta = 0f;
                    sinTheta = -1f;
                    break;
                case ScreenOrientation.PortraitUpsideDown:
                    cosTheta = 0f;
                    sinTheta = 1f;
                    break;
                case ScreenOrientation.LandscapeLeft:
                    cosTheta = 1f;
                    sinTheta = 0f;
                    break;
                case ScreenOrientation.LandscapeRight:
                    cosTheta = -1f;
                    sinTheta = 0f;
                    break;
            }

            m_DisplayTransform.m00 = cosTheta;
            m_DisplayTransform.m01 = sinTheta;
            m_DisplayTransform.m10 = sinTheta;
            m_DisplayTransform.m11 = -cosTheta;
        }

        public override void SetupCamera(Camera camera)
        {
            camera.gameObject.SetActive(false);
            m_BackgroundRenderer = camera.gameObject.AddComponent<ARCoreBackgroundRenderer>();
            m_BackgroundRenderer.BackgroundMaterial = Resources.Load("Materials/ARBackground", typeof(Material)) as Material;
            camera.gameObject.SetActive(true);
        }

        public override void UpdateCamera(Camera camera)
        {
            if (Screen.orientation == m_CachedScreenOrientation)
                return;

            CalculateDisplayTransform();
            m_CachedScreenOrientation = Screen.orientation;
        }

        private bool PlaneUpdated(TrackedPlane tp, BoundedPlane bp)
        {
            var extents = (tp.ExtentX != bp.extents.x || tp.ExtentZ != bp.extents.y);
            var rotation = tp.Rotation != bp.rotation;
            var position = tp.Position != bp.center;
            return (extents || rotation || position);
        }

        public override void Update()
        {
            if (Frame.TrackingState != TrackingState.Tracking)
                return;

            Frame.GetPlanes(m_TrackedPlaneBuffer);
            foreach (var trackedPlane in m_TrackedPlaneBuffer)
            {
                BoundedPlane boundedPlane;
                if (m_TrackedPlanes.TryGetValue(trackedPlane, out boundedPlane))
                {
                    // remove any subsumed planes
                    if (trackedPlane.SubsumedBy != null)
                    {
                        OnPlaneRemoved(boundedPlane);
                        m_TrackedPlanes.Remove(trackedPlane);
                    }
                    // update any planes with changed extents
                    else if (PlaneUpdated(trackedPlane, boundedPlane))
                    {
                        boundedPlane.center = trackedPlane.Position;
                        boundedPlane.rotation = trackedPlane.Rotation;
                        boundedPlane.extents.x = trackedPlane.ExtentX;
                        boundedPlane.extents.y = trackedPlane.ExtentZ;
                        OnPlaneUpdated(boundedPlane);
                    }
                }
                // add any new planes
                else
                {
                    boundedPlane = new BoundedPlane()
                    {
                        id = Guid.NewGuid().ToString(),
                        center = trackedPlane.Position,
                        rotation = trackedPlane.Rotation,
                        extents = new Vector2(trackedPlane.ExtentX, trackedPlane.ExtentZ)
                    };

                    m_TrackedPlanes.Add(trackedPlane, boundedPlane);
                    OnPlaneAdded(boundedPlane);
                }
            }

            // Check for planes that were removed from the tracked plane list
            List<TrackedPlane> planesToRemove = new List<TrackedPlane>();
            foreach (var kvp in m_TrackedPlanes)
            {
                var trackedPlane = kvp.Key;
                if (!m_TrackedPlaneBuffer.Exists(x => x == trackedPlane))
                {
                    OnPlaneRemoved(kvp.Value);
                    planesToRemove.Add(trackedPlane);
                }
            }

            foreach (var plane in planesToRemove)
                m_TrackedPlanes.Remove(plane);
        }
    }
}
