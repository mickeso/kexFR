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
    using System.Drawing;

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

        private bool saveModel = false;

        private string name = null;


        private int timeLeft = 10;

        private bool visited = false;

        static System.Windows.Forms.Timer aTimer = new System.Windows.Forms.Timer();

        private int number = 1;
        



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

                        if (faceTrackFrame.TrackSuccessful)
                        {

                            if (!visited)
                            {
                                visited = true;
                                //counter.Text = "60 seconds";
                                aTimer.Interval = 1000;
                                aTimer.Tick += new EventHandler(aTimer_Tick);
                                aTimer.Start();
                            }                            
                            if (saveModel)
                            {
                                saveDepthImagebmp(depthImageFrame);
                                saveColorImage(colorImageFrame.Width, colorImageFrame.Height, (colorImageFrame.Width * Bgr32BytesPerPixel));
                                saveFaceModel();
                                
                            }
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
        
        private void saveColorImage(int width, int height, int stride)
        {

            BitmapEncoder encoder = new PngBitmapEncoder();
            BitmapFrame bmf = BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, this.colorImage, stride));
            encoder.Frames.Add(bmf);

            string path = System.IO.Path.Combine(@"C:\Kex\data\", name + number + ".png");

            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Create))
                {
                    encoder.Save(fs);
                }
            }
            catch (IOException ioe)
            {
                Console.WriteLine(ioe.ToString());
            }

        }


        private void saveDepthImagebmp(DepthImageFrame depthFrame)
        {
            // Get the min and max reliable depth for the current frame
           //int minDepth = depthFrame.MinDepth;
            int minDepth = 700;
            int maxDepth = 1300;

           //int maxDepth = depthFrame.MaxDepth;
           

            DepthImagePixel[] depthPixels = new DepthImagePixel[depthFrame.PixelDataLength];
            depthFrame.CopyDepthImagePixelDataTo(depthPixels);
            byte[] colorPixels = new byte[depthFrame.PixelDataLength * sizeof(int)];
            
            // Convert the depth to RGB
            int colorPixelIndex = 0;
            for (int i = 0; i < depthPixels.Length; ++i)
            {
                // Get the depth for this pixel
                short depth = depthPixels[i].Depth;
                
                // Note: Using conditionals in this loop could degrade performance.
                // Consider using a lookup table instead when writing production code.
                // See the KinectDepthViewer class used by the KinectExplorer sample
                // for a lookup table example.
                byte intensity = (byte)(depth >= minDepth && depth <= maxDepth ? depth : int.MaxValue);

                // Write out blue byte
                colorPixels[colorPixelIndex++] = intensity;

                // Write out green byte
                colorPixels[colorPixelIndex++] = intensity;

                // Write out red byte                        
                colorPixels[colorPixelIndex++] = intensity;

                // We're outputting BGR, the last byte in the 32 bits is unused so skip it
                // If we were outputting BGRA, we would write alpha here.
                ++colorPixelIndex;
            }


            int stride = (depthFrame.Width * Bgr32BytesPerPixel);
            BitmapEncoder encoder = new PngBitmapEncoder();
            BitmapFrame bmf = BitmapFrame.Create(BitmapSource.Create(depthFrame.Width, depthFrame.Height, 96, 96, PixelFormats.Bgr32, null, colorPixels, stride));

            encoder.Frames.Add(bmf);

            string path = System.IO.Path.Combine(@"C:\Kex\data\", name + number + "d.png");

            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Create))
                {
                    encoder.Save(fs);
                }
            }
            catch (IOException ioe)
            {
                Console.WriteLine(ioe.ToString());
            }

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

        private void Button_Click_Save(object sender, RoutedEventArgs e)
        {
            name = text.GetLineText(0);
            this.saveModel = true;
        }


        private void Button_Click_Reset(object sender, RoutedEventArgs e)
        {
            faceTracker.ResetTracking();
            counter.Text = "5 seconds";
            this.saveModel = false;
            this.visited = false;
            this.number = 1;
            this.timeLeft = 60;
            button.IsEnabled = false;
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

                if (faceTrackFrame.TrackSuccessful && number <= 3)
                {
                    EnumIndexableCollection<FeaturePoint, Vector3DF> fpA = faceTrackFrame.Get3DShape();                    
                    MessageBox.Show("saved model " + number + " for " + name);
                    //saveColorImage(name);
                    // save to file :

                    System.IO.File.WriteAllText(@"C:\Kex\data\" + name + number + ".txt", name);

                    using (System.IO.StreamWriter file = new System.IO.StreamWriter(@"C:\Kex\data\" + name + number + ".txt"))

                    {
                        foreach (Vector3DF fp in fpA)
                        {

                            file.WriteLine("" + fp.X + " , " + fp.Y + " , " + fp.Z);

                        }
                    }
                    number++;

                }


            }
        }

        private void aTimer_Tick(object sender, EventArgs e)
        {
            if (this.timeLeft > 0)
            {
                // Display the new time left 
                // by updating the Time Left label.
                timeLeft = timeLeft - 1;
                counter.Text = timeLeft + " seconds";
            }
            else
            {
                aTimer.Stop();
                counter.Text = "It's picture time!";
                button.IsEnabled = true;
            }
        }



    }
}