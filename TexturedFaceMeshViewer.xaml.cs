// --------------------------------------------------------------------------------------------------------------------
// <copyright file="TexturedFaceMeshViewer.xaml.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

namespace FaceTracking3D
{
    using System;
    using System.Diagnostics;
    using System.Linq;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
    using System.Windows.Media.Imaging;
    using System.Windows.Media.Media3D;
    using Microsoft.Kinect;
    using Microsoft.Kinect.Toolkit.FaceTracking;

    using Point = System.Windows.Point;
    using System.IO;

    /// <summary>
    /// Interaction logic for TexturedFaceMeshViewer.xaml
    /// </summary>
    public partial class TexturedFaceMeshViewer : UserControl, IDisposable
    {
        public static readonly DependencyProperty KinectProperty = DependencyProperty.Register(
            "Kinect",
            typeof(KinectSensor),
            typeof(TexturedFaceMeshViewer),
            new UIPropertyMetadata(
                null,
                (o, args) =>
                ((TexturedFaceMeshViewer)o).OnKinectChanged((KinectSensor)args.OldValue, (KinectSensor)args.NewValue)));

        private static readonly int Bgr32BytesPerPixel = (PixelFormats.Bgr32.BitsPerPixel + 7) / 8;

        private byte[] colorImage;

        private ColorImageFormat colorImageFormat = ColorImageFormat.Undefined;

        private WriteableBitmap colorImageWritableBitmap;

        private short[] depthImage;

        private DepthImageFormat depthImageFormat = DepthImageFormat.Undefined;

        private FaceTracker faceTracker;

        private Skeleton[] skeletonData;

        private int trackingId = -1;

        private FaceTriangle[] triangleIndices;

        private float[,] facePointsADist = null;

        private bool saveModel = false;

        private string name = null;

        public TexturedFaceMeshViewer()
        {
            this.DataContext = this;
            this.InitializeComponent();
        }

        public KinectSensor Kinect
        {
            get
            {
                return (KinectSensor)this.GetValue(KinectProperty);
            }

            set
            {
                this.SetValue(KinectProperty, value);
            }
        }

        public void Dispose()
        {
            this.DestroyFaceTracker();
        }

        private void AllFramesReady(object sender, AllFramesReadyEventArgs allFramesReadyEventArgs)
        {
            ColorImageFrame colorImageFrame = null;
            DepthImageFrame depthImageFrame = null;
            SkeletonFrame skeletonFrame = null;

            try
            {
                colorImageFrame = allFramesReadyEventArgs.OpenColorImageFrame();
                depthImageFrame = allFramesReadyEventArgs.OpenDepthImageFrame();
                skeletonFrame = allFramesReadyEventArgs.OpenSkeletonFrame();

                if (colorImageFrame == null || depthImageFrame == null || skeletonFrame == null)
                {
                    return;
                }

                // Check for changes in any of the data this function is receiving
                // and reset things appropriately.
                if (this.depthImageFormat != depthImageFrame.Format)
                {
                    this.DestroyFaceTracker();
                    this.depthImage = null;
                    this.depthImageFormat = depthImageFrame.Format;
                }

                if (this.colorImageFormat != colorImageFrame.Format)
                {
                    this.DestroyFaceTracker();
                    this.colorImage = null;
                    this.colorImageFormat = colorImageFrame.Format;
                    this.colorImageWritableBitmap = null;
                    this.ColorImage.Source = null;
                    this.theMaterial.Brush = null;
                }

                if (this.skeletonData != null && this.skeletonData.Length != skeletonFrame.SkeletonArrayLength)
                {
                    this.skeletonData = null;
                }

                // Create any buffers to store copies of the data we work with
                if (this.depthImage == null)
                {
                    this.depthImage = new short[depthImageFrame.PixelDataLength];
                }

                if (this.colorImage == null)
                {
                    this.colorImage = new byte[colorImageFrame.PixelDataLength];
                }

                if (this.colorImageWritableBitmap == null)
                {
                    this.colorImageWritableBitmap = new WriteableBitmap(
                        colorImageFrame.Width, colorImageFrame.Height, 96, 96, PixelFormats.Bgr32, null);
                    this.ColorImage.Source = this.colorImageWritableBitmap;
                    this.theMaterial.Brush = new ImageBrush(this.colorImageWritableBitmap)
                        {
                            ViewportUnits = BrushMappingMode.Absolute
                        };
                }

                if (this.skeletonData == null)
                {
                    this.skeletonData = new Skeleton[skeletonFrame.SkeletonArrayLength];
                }

                // Copy data received in this event to our buffers.
                colorImageFrame.CopyPixelDataTo(this.colorImage);
                depthImageFrame.CopyPixelDataTo(this.depthImage);
                skeletonFrame.CopySkeletonDataTo(this.skeletonData);
                this.colorImageWritableBitmap.WritePixels(
                    new Int32Rect(0, 0, colorImageFrame.Width, colorImageFrame.Height),
                    this.colorImage,
                    colorImageFrame.Width * Bgr32BytesPerPixel,
                    0);

                // Find a skeleton to track.
                // First see if our old one is good.
                // When a skeleton is in PositionOnly tracking state, don't pick a new one
                // as it may become fully tracked again.
                Skeleton skeletonOfInterest =
                    this.skeletonData.FirstOrDefault(
                        skeleton =>
                        skeleton.TrackingId == this.trackingId
                        && skeleton.TrackingState != SkeletonTrackingState.NotTracked);

                if (skeletonOfInterest == null)
                {
                    // Old one wasn't around.  Find any skeleton that is being tracked and use it.
                    skeletonOfInterest =
                        this.skeletonData.FirstOrDefault(
                            skeleton => skeleton.TrackingState == SkeletonTrackingState.Tracked);

                    if (skeletonOfInterest != null)
                    {
                        // This may be a different person so reset the tracker which
                        // could have tuned itself to the previous person.
                        if (this.faceTracker != null)
                        {
                            this.faceTracker.ResetTracking();
                        }

                        this.trackingId = skeletonOfInterest.TrackingId;
                    }
                }

                if (skeletonOfInterest != null && skeletonOfInterest.TrackingState == SkeletonTrackingState.Tracked)
                {
                    if (this.faceTracker == null)
                    {
                        try
                        {
                            this.faceTracker = new FaceTracker(this.Kinect);
                        }
                        catch (InvalidOperationException)
                        {
                            // During some shutdown scenarios the FaceTracker
                            // is unable to be instantiated.  Catch that exception
                            // and don't track a face.
                            Debug.WriteLine("AllFramesReady - creating a new FaceTracker threw an InvalidOperationException");
                            this.faceTracker = null;
                        }
                    }

                    if (this.faceTracker != null)
                    {
                        FaceTrackFrame faceTrackFrame = this.faceTracker.Track(
                            this.colorImageFormat,
                            this.colorImage,
                            this.depthImageFormat,
                            this.depthImage,
                            skeletonOfInterest);

                        if (faceTrackFrame.TrackSuccessful && saveModel)
                        {
                            saveFaceModel();
                                                       
                        }
                    }
                }
                else
                {
                    this.trackingId = -1;
                }

            }
            finally
            {
                if (colorImageFrame != null)
                {
                    colorImageFrame.Dispose();
                }

                if (depthImageFrame != null)
                {
                    depthImageFrame.Dispose();
                }

                if (skeletonFrame != null)
                {
                    skeletonFrame.Dispose();
                }
            }
        }

        private void saveDepthImagebmp()
        {
            
        }

        private void DestroyFaceTracker()
        {
            if (this.faceTracker != null)
            {
                this.faceTracker.Dispose();
                this.faceTracker = null;
            }
        }

        private void OnKinectChanged(KinectSensor oldSensor, KinectSensor newSensor)
        {
            if (oldSensor != null)
            {
                try
                {
                    oldSensor.AllFramesReady -= this.AllFramesReady;

                    this.DestroyFaceTracker();
                }
                catch (InvalidOperationException)
                {
                    // KinectSensor might enter an invalid state while enabling/disabling streams or stream features.
                    // E.g.: sensor might be abruptly unplugged.
                }
            }

            if (newSensor != null)
            {
                try
                {
                    this.faceTracker = new FaceTracker(this.Kinect);

                    newSensor.AllFramesReady += this.AllFramesReady;
                }
                catch (InvalidOperationException)
                {
                    // KinectSensor might enter an invalid state while enabling/disabling streams or stream features.
                    // E.g.: sensor might be abruptly unplugged.
                }
            }
        }

        private float[,] calcDist(EnumIndexableCollection<FeaturePoint, Vector3DF> fp)
        {
            int fpSize = fp.Count();
            Vector3DF[] temp = new Vector3DF[fpSize];
            float[,] fpRes = new float[fpSize, fpSize];
            int k = 0;
            foreach (Vector3DF p in fp)
            {
                temp[k] = p;
                k++;
            }
            for (int i = 0; i < (fpSize - 1); i++)
            {
                for (int j = 0; j < (fpSize - 1); j++)
                {
                    if (i == j) { }
                    // Do stuff to temp!
                    else
                    {
                        fpRes[i, j] = Math.Abs((float)Math.Sqrt((float)Math.Pow(temp[i].X - temp[j].X, 2) + (float)Math.Pow(temp[i].Y - temp[j].Y, 2) + (float)Math.Pow(temp[i].Z - temp[j].Z, 2)));
                    }
                }
            }
            return fpRes;
        }
        private int pointsCount(EnumIndexableCollection<FeaturePoint, Vector3DF> fp)
        {
            int size = fp.Count();
            return size;
        }

        private float calcDiff(float[,] a, float[,] b)
        {
            float result = 0;
            for (int i = 0; i < 121; i++)
            {
                for (int j = 0; j < 121; j++)
                {
                    result += Math.Abs(a[i, j] - b[i, j]);
                }
            }
            return result;
        }

        private void Button_Click_Reset(object sender, RoutedEventArgs e)
        {
            saveModel = true;
        }

        private void saveFaceModel()
        {
            this.saveModel = false;          //notify model is saved:
            Skeleton skeletonOfInterest =
                     this.skeletonData.FirstOrDefault(
                         skeleton =>
                         skeleton.TrackingId == this.trackingId
                         && skeleton.TrackingState != SkeletonTrackingState.NotTracked);

            if (skeletonOfInterest != null && skeletonOfInterest.TrackingState == SkeletonTrackingState.Tracked)
            {
                FaceTrackFrame faceTrackFrame = this.faceTracker.Track(
                                this.colorImageFormat,
                                this.colorImage,
                                this.depthImageFormat,
                                this.depthImage,
                                skeletonOfInterest);

                if (faceTrackFrame.TrackSuccessful)
                {
                    EnumIndexableCollection<FeaturePoint, Vector3DF> fpA = faceTrackFrame.Get3DShape();
                    EnumIndexableCollection<FeaturePoint, PointF> fpT = faceTrackFrame.GetProjected3DShape();



                    facePointsADist = calcDist(fpA);
                    //howManyPointsA = pointsCount(fpA);
                    //facePointsADist[0] = (float) Math.Sqrt(Math.Pow(fpA[23].X - fpA[56].X, 2) + Math.Pow(fpA[23].Y - fpA[56].Y, 2) + Math.Pow(fpA[23].Z - fpA[56].Z, 2));
                    // MessageBox.Show("saved"+faceTrackFrame.GetTriangles()[0].Second);

                    name = text.GetLineText(0);
                    MessageBox.Show("saved model for " + name);

                    // save to file :
                    System.IO.File.WriteAllText(@"C:\Kex\data\"+name+".txt", name);

                    // Example #3: Write only some strings in an array to a file. 
                    // The using statement automatically closes the stream and calls  
                    // IDisposable.Dispose on the stream object. 
                    using (System.IO.StreamWriter file = new System.IO.StreamWriter(@"C:\Kex\data\"+name+".txt"))
                    {
                        foreach (Vector3DF fp in fpA)
                        {

                            file.WriteLine("" + fp.X + " , " + fp.Y + " , " + fp.Z);

                        }
                    }





                }


            }
        }
    }
}