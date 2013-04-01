///////////////////////////////////////////////////////////////////////////////////
// Open 3D Model Viewer (open3mod) (v0.1)
// [Scene.cs]
// (c) 2012-2013, Alexander C. Gessler
//
// Licensed under the terms and conditions of the 3-clause BSD license. See
// the LICENSE file in the root folder of the repository for the details.
//
// HIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
// ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE 
// DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
// ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
// (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; 
// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND 
// ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT 
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS 
// SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
///////////////////////////////////////////////////////////////////////////////////


using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

using Assimp;
using Assimp.Configs;
using OpenTK;


namespace open3mod
{
    /// <summary>
    /// Represents a 3D scene/asset loaded through assimp.
    /// 
    /// Basically, this class contains the aiScene plus some auxiliary structures
    /// for drawing. Since assimp is the only source for model data and this is
    /// only a viewer, we ignore the recommendation of the assimp docs and use
    /// its data structures (almost) directly for rendering.
    /// </summary>
    public sealed class Scene : IDisposable
    {
        private readonly string _file;
        private readonly string _baseDir;
       
        private readonly Assimp.Scene _raw;
        private Vector3 _sceneCenter;
        private Vector3 _sceneMin;
        private Vector3 _sceneMax;       
        private readonly LogStore _logStore;

        private readonly TextureSet _textureSet;

        private readonly MaterialMapper _mapper;
        private readonly ISceneRenderer _renderer;


        /// <summary>
        /// Source file name with full path
        /// </summary>
        public string File
        {
            get { return _file; }
        }

        /// <summary>
        /// Folder in which the source file resides, the "working directory"
        /// for the scene if you want.
        /// </summary>
        public string BaseDir
        {
            get { return _baseDir; }
        }


        /// <summary>
        /// Obtain the "raw" scene data as imported by Assimp
        /// </summary>
        public Assimp.Scene Raw
        {
            get { return _raw; }
        }


        public Vector3 SceneCenter
        {
            get { return _sceneCenter; }
        }


        public LogStore LogStore
        {
            get { return _logStore; }
        }


        public MaterialMapper MaterialMapper
        {
            get { return _mapper; }
        }


        public SceneAnimator SceneAnimator
        {
            get { return _animator; }
        }


        public Dictionary<Node, List<Mesh>> VisibleMeshesByNode
        {
            get { return _meshesToShow; }
        }


        public TextureSet TextureSet
        {
            get { return _textureSet; }
        }


        public bool IsIncompleteScene
        {
            get { return _incomplete; }
        }

        public int TotalVertexCount
        {
            get { return _totalVertexCount; }
        }

        public int TotalTriangleCount
        {
            get { return _totalTriangleCount; }
        }

        public int TotalLineCount
        {
            get { return _totalLineCount; }
        }

        public int TotalPointCount
        {
            get { return _totalPointCount; }
        }

        public string StatsString
        {
            get {
                var s = " Raw Loading Time: " + LoadingTime + " ms - ";
                
                s += TotalVertexCount + " Vertices, " + TotalTriangleCount + " Triangles";
                if (TotalLineCount > 0)
                {
                    s += ", " + TotalLineCount + " Lines";
                }
                if (TotalPointCount > 0)
                {
                    s += ", " + TotalLineCount + " Points";
                }

                
                return s;
            }
        }

        public long LoadingTime
        {
            get { return _loadingTime; }
        }


        private bool _texturesChanged = false;
        private readonly SceneAnimator _animator;
        private double _accumulatedTimeDelta;

        private bool _nodesToShowChanged = true;
        private Dictionary<Node, List<Mesh>> _meshesToShow;
        private bool _overrideSkeleton;
        private readonly bool _incomplete;

        private readonly int _totalVertexCount;
        private readonly int _totalTriangleCount;
        private readonly int _totalLineCount;
        private readonly int _totalPointCount;
        private long _loadingTime;

        /// <summary>
        /// Construct a scene given a file name, throw if loading fails
        /// </summary>
        /// <param name="file">File name to be loaded</param>
        public Scene(string file)
        {
            _file = file;
            _baseDir = Path.GetDirectoryName(file);
  
            _logStore = new LogStore();

            var stopwatch = new Stopwatch();
            stopwatch.Start();

            try
            {
                using (var imp = new AssimpImporter {VerboseLoggingEnabled = true})
                {
                    imp.AttachLogStream((new LogPipe(_logStore)).GetStream());

                    // Assimp configuration:

                    //  - if no normals are present, generate them using a threshold
                    //    angle of 66 degrees.
                    imp.SetConfig(new NormalSmoothingAngleConfig(66.0f));

                    //  - request lots of post processing steps, the details of which
                    //    can be found in the TargetRealTimeMaximumQuality docs.
                    _raw = imp.ImportFile(file, PostProcessPreset.TargetRealTimeMaximumQuality);
                    if (_raw == null)
                    {
                        Dispose();
                        throw new Exception("failed to read file: " + file);
                    }

                    _incomplete = _raw.SceneFlags.HasFlag(SceneFlags.Incomplete);
                }
            }
            catch(AssimpException ex)
            {
                Dispose();
                throw new Exception("failed to read file: " + file + " (" + ex.Message + ")");               
            }

            stopwatch.Stop();
            _loadingTime = stopwatch.ElapsedMilliseconds;

            _mapper = new MaterialMapper(this); 
            _animator = new SceneAnimator(this);
            _textureSet = new TextureSet(BaseDir);
            LoadTextures();

            // compute a bounding box (AABB) for the scene we just loaded
            ComputeBoundingBox(out _sceneMin, out _sceneMax);

            CountVertsAndFaces(out _totalVertexCount, out _totalTriangleCount, out _totalLineCount, out _totalPointCount);

            _renderer = new SceneRendererClassicGl(this, _sceneMin, _sceneMax);
        }


        /// <summary>
        /// Sets which parts of the scene are visible.
        /// 
        /// See ISceneRenderer.Render (visibleNodesByMesh parameters)
        /// for a description of how the visible set is determined.
        /// 
        /// </summary>
        /// <param name="meshFilter">See ISceneRenderer.Render (visibleNodesByMesh parameter)</param>
        public void SetVisibleNodes(Dictionary<Node, List<Mesh>> meshFilter)
        {
            _meshesToShow = meshFilter;
            _nodesToShowChanged = true;
        }


        /// <summary>
        /// To be called once per frame to do non-rendering jobs such as updating 
        /// animations.
        /// </summary>
        /// <param name="delta">Real-world time delta in seconds</param>
        /// <param name="silent">True if the scene is in background and won't
        ///    be rendered this frame. It may then be possible to implement
        ///    a cheaper update.</param>
        public void Update(double delta, bool silent = false)
        {
            if(silent)
            {
                _accumulatedTimeDelta += delta;
                return;
            }
            _animator.Update(delta + _accumulatedTimeDelta);
            _accumulatedTimeDelta = 0.0;

            _renderer.Update(delta);
        }

        private bool _wantSetTexturesChanged;
        private readonly object _texChangeLock = new object();

        /// <summary>
        /// Call once per frame to render the scene to the current viewport.
        /// </summary>
        public void Render(UiState state, ICameraController cam)
        {
            RenderFlags flags = 0;
          
            if (state.ShowNormals)
            {
                flags |= RenderFlags.ShowNormals;
            }
            if (state.ShowBBs)
            {
                flags |= RenderFlags.ShowBoundingBoxes;
            }
            if (state.ShowSkeleton || _overrideSkeleton)
            {
                flags |= RenderFlags.ShowSkeleton;
            }
            if (state.RenderLit)
            {
                flags |= RenderFlags.Shaded;
            }
            if (state.RenderTextured)
            {
                flags |= RenderFlags.Textured;
            }
            if (state.RenderWireframe)
            {
                flags |= RenderFlags.Wireframe;
            }
            
            flags |= RenderFlags.ShowGhosts;

            _wantSetTexturesChanged = false;
            _renderer.Render(cam, _meshesToShow, _nodesToShowChanged, _texturesChanged, flags);

            lock (_texChangeLock)
            {
                if (!_wantSetTexturesChanged)
                {
                    _texturesChanged = false;
                }
            }

            _nodesToShowChanged = false;
        }


        /// <summary>
        /// Populates the TextureSet with all the textures required for the scene.
        /// </summary>
        private void LoadTextures()
        {
            var materials = _raw.Materials;
            foreach (var mat in materials)
            {
                var textures = mat.GetAllTextures();
                foreach (var tex in textures)
                {
                    var path = tex.FilePath;
                    Assimp.Texture embeddedSource = null;
                    if(path.StartsWith("*"))
                    {
                        // Embedded texture, following the asterisk is the zero-based
                        // index of the data source in Assimp.Scene.Textures
                        uint index;
                        if(Raw.HasTextures && uint.TryParse(path.Substring(1), out index) && index < Raw.TextureCount)
                        {
                            embeddedSource = Raw.Textures[index];
                        }
                        // else: just add the name to the texture set without specifying
                        // a data source, the texture will then be in a failed state 
                        // but users can still replace it manually.
                    }

                    TextureSet.Add(tex.FilePath, embeddedSource);
                }
            }

            TextureSet.AddCallback((name, tex) => {
                lock (_texChangeLock)
                {
                    _wantSetTexturesChanged = true;
                    _texturesChanged = true;
                }
                return true;
            });
        }



        /// <summary>
        /// Counts vertices, points, lines and triangles in the scene.
        /// </summary>
        /// <param name="totalVertexCount"></param>
        /// <param name="totalTriangleCount"></param>
        /// <param name="totalLineCount"></param>
        /// <param name="totalPointCount"></param>
        private void CountVertsAndFaces(out int totalVertexCount, out int totalTriangleCount, out int totalLineCount, out int totalPointCount)
        {
            totalVertexCount = 0;
            totalTriangleCount = 0;
            totalLineCount = 0;
            totalPointCount = 0;

            for (var i = 0; i < _raw.MeshCount; ++i)
            {
                var mesh = _raw.Meshes[i];
                totalVertexCount += mesh.VertexCount;

                for (var j = 0; j < mesh.FaceCount; ++j)
                {
                    var face = mesh.Faces[j];
                    switch(face.IndexCount) // /^_^/
                    {
                        case 1:
                            ++totalPointCount;
                            break;
                        case 2:
                            ++totalLineCount;
                            break;
                        case 3:
                            ++totalTriangleCount;
                            break;
                        default:
                            Debug.Assert(false);
                            break;
                    }
                }
            }
        }


        /// <summary>
        /// Calculates the smallest AABB that encloses the scene.
        /// </summary>
        /// <param name="sceneMin"></param>
        /// <param name="sceneMax"></param>
        private void ComputeBoundingBox(out Vector3 sceneMin, out Vector3 sceneMax)
        {
            sceneMin = new Vector3(1e10f, 1e10f, 1e10f);
            sceneMax = new Vector3(-1e10f, -1e10f, -1e10f);
            Matrix4 identity = Matrix4.Identity;

            ComputeBoundingBox(_raw.RootNode, ref sceneMin, ref sceneMax, ref identity);
            _sceneCenter = (_sceneMin + _sceneMax) / 2.0f;
        }


        /// <summary>
        /// Helper for ComputeBoundingBox(out Vector3 sceneMin, out Vector3 sceneMax)
        /// </summary>
        /// <param name="node"></param>
        /// <param name="min"></param>
        /// <param name="max"></param>
        /// <param name="trafo"></param>
        private void ComputeBoundingBox(Node node, ref Vector3 min, ref Vector3 max, ref Matrix4 trafo)
        {
            Matrix4 prev = trafo;
            trafo = Matrix4.Mult(prev, AssimpToOpenTk.FromMatrix(node.Transform));

            if (node.HasMeshes)
            {
                foreach (int index in node.MeshIndices)
                {
                    Mesh mesh = _raw.Meshes[index];
                    for (int i = 0; i < mesh.VertexCount; i++)
                    {
                        Vector3 tmp = AssimpToOpenTk.FromVector(mesh.Vertices[i]);
                        Vector3.Transform(ref tmp, ref trafo, out tmp);

                        min.X = Math.Min(min.X, tmp.X);
                        min.Y = Math.Min(min.Y, tmp.Y);
                        min.Z = Math.Min(min.Z, tmp.Z);

                        max.X = Math.Max(max.X, tmp.X);
                        max.Y = Math.Max(max.Y, tmp.Y);
                        max.Z = Math.Max(max.Z, tmp.Z);
                    }
                }
            }

            for (int i = 0; i < node.ChildCount; i++)
            {
                ComputeBoundingBox(node.Children[i], ref min, ref max, ref trafo);
            }
            trafo = prev;
        }


        /// <summary>
        /// 
        /// </summary>
        public void Dispose()
        {
            if (_textureSet != null)
            {
                _textureSet.Dispose();
            }

            if (_renderer != null)
            {
                _renderer.Dispose();
            }

            GC.SuppressFinalize(this);
        }

#if DEBUG
        ~Scene()
        {
            // OpenTk is unsafe from here, explicit Dispose() is required.
            Debug.Assert(false);
        }
#endif


        /// <summary>
        /// Turns skeleton visualization on even if it is off in the UI.
        /// </summary>
        /// <param name="overrideSkeleton">true to always show skeleton
        ///    visualization, false to take the UI's settings</param>
        public void SetSkeletonVisibleOverride(bool overrideSkeleton)
        {
            _overrideSkeleton = overrideSkeleton;
        }
    }


    /// <summary>
    /// Utility class to generate an assimp logstream to capture the logging
    /// into a LogStore
    /// </summary>
    class LogPipe 
    {
        private readonly LogStore _logStore;
        private Stopwatch _timer;

        public LogPipe(LogStore logStore) 
        {
            _logStore = logStore;
            
        }

        public LogStream GetStream()
        {
            return new LogStream(LogStreamCallback);
        }


        private void LogStreamCallback(string msg, string userdata)
        {
            // Start timing with the very first logging messages. This
            // is relatively reliable because assimp writes a log header
            // as soon as it starts processing a file.
            if (_timer == null)
            {
                _timer = new Stopwatch();
                _timer.Start(); 
              
            }

            long millis = _timer.ElapsedMilliseconds;

            // Unfortunately, assimp-net does not wrap assimp's native
            // logging interfaces so log streams (which receive
            // pre-formatted messages) are the only way to capture
            // the logging. This means we have to recover the original
            // information (such as log level and the thread/job id) 
            // from the string contents.



            int start = msg.IndexOf(':');
            if(start == -1)
            {
                // this should not happen but nonetheless check for it
                //Debug.Assert(false);
                return;
            }

            var cat = LogStore.Category.Info;
            if (msg.StartsWith("Error, "))
            {
                cat = LogStore.Category.Error;        
            }
            else if (msg.StartsWith("Debug, "))
            {
                cat = LogStore.Category.Debug;
            }
            else if (msg.StartsWith("Warn, "))
            {
                cat = LogStore.Category.Warn;
            }
            else if (msg.StartsWith("Info, "))
            {
                cat = LogStore.Category.Info;
            }
            else
            {
                // this should not happen but nonetheless check for it
                //Debug.Assert(false);
                return;
            }

            int startThread = msg.IndexOf('T');
            if (startThread == -1 || startThread >= start)
            {
                // this should not happen but nonetheless check for it
                //Debug.Assert(false);
                return;
            }

            int threadId = 0;
            int.TryParse(msg.Substring(startThread + 1, start - startThread - 1), out threadId);

            _logStore.Add(cat, msg.Substring(start + 1), millis, threadId);
        }
    }
}

/* vi: set shiftwidth=4 tabstop=4: */ 