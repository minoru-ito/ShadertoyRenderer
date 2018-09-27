#region usings
using System;
using System.ComponentModel.Composition;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

using VVVV.PluginInterfaces.V1;
using VVVV.PluginInterfaces.V2;
using VVVV.PluginInterfaces.V2.EX9;
using VVVV.Utils.VColor;
using VVVV.Utils.VMath;
using VVVV.Utils.SlimDX;
using VVVV.Core.Logging;

using FeralTic.DX11;
using FeralTic.DX11.Resources;

using SlimDX;
using SlimDX.Direct3D9;
//using SlimDX.DXGI;
//using SlimDX.Direct3D11;

using OpenTK;
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL;
#endregion usings

namespace VVVV.Nodes
{
    namespace shadertoy
    {
        #region PluginInfo
        [PluginInfo(Name = "Renderer", Category = "shadertoy", Version = "DX9 FileTexture", AutoEvaluate = true)]
        #endregion PluginInfo
        public class shadertoyRendererDX9Node : UserControl, IPluginEvaluate, IPartImportsSatisfiedNotification
        {
            #region fields & pins

            public class Info
            {
                public int Width;
                public int Height;
            }

            [Input("Play")]
            public ISpread<bool> FInPlay;

            [Input("Fragment Shader")]
            public IDiffSpread<string> FInFragmentShader;

            [Input("Filename", StringType = StringType.Filename)]
            public IDiffSpread<string> FInFilename;

            [Output("Texture Out")]
            public ISpread<TextureResource<Info>> FOutTexture;

            [Output("Log")]
            public ISpread<string> FOutLog;

            [Import()]
            public ILogger FLogger;

            [Import()]
            public IHDEHost FHDEHost;

            // gui controls
            GLControl glControl;

            // shader
            int shaderProgram = -1;
            int vertexShader = -1;
            int fragmentShader = -1;

            // texture for GLSL
            int[] tex = new int[4];

            // texture for vvvv
            int width, height;
            byte[] pixelBuffer, flippedBuffer;
            int channels = 4;

            // mouse pos
            int dragX, dragY, clickX, clickY;

            // frame counter
            int frames;

            // frame sec
            double frameTimeDelay;

            // flag
            bool initialized = false;
            bool resized = false;
            bool disposed = false;
            bool invalidate = false;

            #endregion fields & pins

            #region constructor and init

            public shadertoyRendererDX9Node()
            {
                //
            }

            public void OnImportsSatisfied()
            {
                initialized = false;
                resized = false;

                FOutTexture.SliceCount = 0;

                //setup the gui
                InitializeComponent();
            }

            void InitializeComponent()
            {
                //clear controls in case init is called multiple times
                Controls.Clear();
                this.SuspendLayout();

                glControl = new GLControl();
                glControl.BackColor = System.Drawing.Color.Black;
                glControl.Dock = System.Windows.Forms.DockStyle.Fill;
                glControl.Location = new Point(0, 0);
                //glControl.Name = "glControl";
                //glControl.Size = new Size(640, 480);
                glControl.TabIndex = 0;
                glControl.VSync = false;
                glControl.Load += glControl_Load;
                glControl.Paint += glControl_Paint;
                glControl.Resize += glControl_Resize;
                glControl.Disposed += glControl_Disposed;
                //glControl.KeyPress += glControl_KeyPress;
                //glControl.KeyUp += glControl_KeyUp;
                glControl.MouseDown += glControl_MouseDown;
                glControl.MouseMove += glControl_MouseMove;
                glControl.MouseUp += glControl_MouseUp;

                Controls.Add(glControl);
                this.Resize += OnResize;

                this.ResumeLayout(false);
            }

            #endregion constructor and init

            //called when data for any output pin is requested
            public void Evaluate(int SpreadMax)
            {
                //FLogger.Log(LogType.Debug, FHDEHost.FrameTime.ToString() + "," + FHDEHost.RealTime.ToString());

                FOutTexture.ResizeAndDispose(1, CreateTextureResource);
                var textureResource = FOutTexture[0];
                var info = textureResource.Metadata;

                if (!initialized || FInFragmentShader.IsChanged || FInFilename.IsChanged)
                {
                    textureResource.Dispose();
                    textureResource = CreateTextureResource();
                    info = textureResource.Metadata;

                    Init();
                }

                if (resized)
                {
                    textureResource.Dispose();
                    textureResource = CreateTextureResource();
                    info = textureResource.Metadata;

                    resized = false;
                }

                if (shaderProgram > 0 && FInPlay[0])
                    Render();

                FOutTexture[0] = textureResource;
            }

            #region GLControl events

            void glControl_Load(object sender, EventArgs e)
            {
                if (FInFragmentShader.SliceCount > 0)
                    Init();
            }

            void glControl_Paint(object sender, EventArgs e)
            {
                Render();
            }

            void glControl_Resize(object sender, EventArgs e)
            {
                //
            }

            void glControl_Disposed(object sender, EventArgs e)
            {
                glControl.MakeCurrent();

                for (int i = 0; i < 4; i++)
                {
                    if (tex[i] != -1)
                        GL.DeleteTexture(tex[i]);
                }

                GL.DeleteProgram(shaderProgram);
            }

            void glControl_KeyPress(object sender, System.Windows.Forms.KeyPressEventArgs e)
            {
                //
            }

            void glControl_KeyUp(object sender, KeyEventArgs e)
            {
                //
            }

            void glControl_MouseDown(object sender, System.Windows.Forms.MouseEventArgs e)
            {
                clickX = e.X;
                clickY = glControl.Height - e.Y;    // reverse y
            }

            void glControl_MouseMove(object sender, System.Windows.Forms.MouseEventArgs e)
            {
                // update only when pressed any button
                if (e.Button != MouseButtons.None)
                {
                    dragX = e.X;
                    dragY = glControl.Height - e.Y; // reverse y
                }
            }

            void glControl_MouseUp(object sender, System.Windows.Forms.MouseEventArgs e)
            {
                // reset to 0
                clickX = 0;
                clickY = 0;
            }

            #endregion GLControl events

            void OnResize(object sender, EventArgs e)
            {
                //FLogger.Log(LogType.Message, "OnResize");

                glControl.Size = this.Size;

                this.width = glControl.Size.Width;
                this.height = glControl.Size.Height;
                pixelBuffer = new byte[this.width * this.height * channels];
                flippedBuffer = new byte[pixelBuffer.Length];

                setupViewport();
                resized = true;
            }

            void setupViewport()
            {
                glControl.MakeCurrent();

                int w = glControl.Width;
                int h = glControl.Height;
                GL.MatrixMode(MatrixMode.Projection);
                GL.LoadIdentity();
                GL.Ortho(0, w, 0, h, -1, -1);
                GL.Viewport(0, 0, w, h);
            }

            void Init()
            {
                if (FInFragmentShader.SliceCount == 0)
                    return;

                // for multiple instance
                glControl.MakeCurrent();

                // reset framr counter
                frames = 0;

                // reset frametimedelay
                frameTimeDelay = FHDEHost.FrameTime;

                //GL.ClearColor(Color4.Black);
                setupViewport();

                int status;
                string vs_log, fs_log;

                // vertex shader

                vertexShader = GL.CreateShader(ShaderType.VertexShader);
                string vs = @"
void main(void){
	gl_Position = ftransform();
}
";
                GL.ShaderSource(vertexShader, vs);
                GL.CompileShader(vertexShader);
                GL.GetShaderInfoLog(vertexShader, out vs_log);
                GL.GetShader(vertexShader, ShaderParameter.CompileStatus, out status);
                if (status != 1)
                {
                    FLogger.Log(LogType.Error, "vertex shader error");
                }

                // fragment shader

                fragmentShader = GL.CreateShader(ShaderType.FragmentShader);
                string fsUniform = @"
uniform vec3      iResolution;           // viewport resolution (in pixels)
uniform float     iTime;           // shader playback time (in seconds)
uniform float     iTimeDelta;            // render time (in seconds)
uniform int       iFrame;                // shader playback frame
uniform float     iChannelTime[4];       // channel playback time (in seconds)
uniform vec3      iChannelResolution[4]; // channel resolution (in pixels)
uniform vec4      iMouse;                // mouse pixel coords. xy: current (if MLB down), zw: click
//uniform samplerXX iChannel0..3;          // input channel. XX = 2D/Cube
uniform vec4      iDate;                 // (year, month, day, time in seconds)

uniform sampler2D iChannel0; // Texture #1
uniform sampler2D iChannel1; // Texture #2
uniform sampler2D iChannel2; // Texture #3
uniform sampler2D iChannel3; // Texture #4

";

                string fsMain = @"
void main(void){
	mainImage(gl_FragColor, gl_FragCoord.xy);
}
";

                string fs = FInFragmentShader[0];

                // add uniform & main
                fs = fsUniform + fs + fsMain;

                GL.ShaderSource(fragmentShader, fs);
                GL.CompileShader(fragmentShader);
                GL.GetShaderInfoLog(fragmentShader, out fs_log);
                GL.GetShader(fragmentShader, ShaderParameter.CompileStatus, out status);
                if (status != 1)
                {
                    FLogger.Log(LogType.Error, "fragment shader error");
                }

                if (vs_log == "" && fs_log == "")
                {
                    FOutLog[0] = "OK";
                }
                else {
                    string s = "";
                    if (vs_log != "")
                        s += "vertex shader:\n" + vs_log + "\n";
                    if (fs_log != "")
                        s += "fragment shader:\n" + fs_log;
                    FOutLog[0] = s;
                }

                // shader program

                if (shaderProgram != -1)
                    GL.DeleteProgram(shaderProgram);

                shaderProgram = GL.CreateProgram();
                GL.AttachShader(shaderProgram, vertexShader);
                GL.AttachShader(shaderProgram, fragmentShader);

                GL.DeleteShader(vertexShader);
                GL.DeleteShader(fragmentShader);

                GL.LinkProgram(shaderProgram);
                GL.GetProgram(shaderProgram, GetProgramParameterName.LinkStatus, out status);
                if (status == 0)
                {
                    //throw GL.GetShaderInfoLog(shaderProgram);
                    FLogger.Log(LogType.Error, "shader program error");

                    shaderProgram = -1;
                }
                else
                    GL.UseProgram(shaderProgram);

                // texture
                if (FInFilename.SliceCount > 0)
                    TextureSetup();
                else
                    tex[0] = tex[1] = tex[2] = tex[3] = -1;

                initialized = true;
            }

            void TextureSetup()
            {
                tex[0] = tex[1] = tex[2] = tex[3] = -1;

                for (int i = 0; i < FInFilename.SliceCount; i++)
                {
                    GL.GenTextures(1, out tex[i]);
                    GL.BindTexture(TextureTarget.Texture2D, tex[i]);

                    try
                    {
                        using (Bitmap bitmap = new Bitmap(FInFilename[i]))
                        {
                            BitmapData bmd = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                                ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, bmd.Width, bmd.Height, 0,
                                OpenTK.Graphics.OpenGL.PixelFormat.Bgra, PixelType.UnsignedByte, bmd.Scan0);
                            bitmap.UnlockBits(bmd);

                            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                        }
                    }
                    catch (Exception e)
                    {
                        FLogger.Log(LogType.Error, e.Message);

                        tex[i] = -1;
                    }
                }

                if (tex[0] != -1)
                    BindTexture(ref tex[0], TextureUnit.Texture0, "iChannel0");
                if (tex[1] != -1)
                    BindTexture(ref tex[1], TextureUnit.Texture1, "iChannel1");
                if (tex[2] != -1)
                    BindTexture(ref tex[2], TextureUnit.Texture2, "iChannel2");
                if (tex[3] != -1)
                    BindTexture(ref tex[3], TextureUnit.Texture3, "iChannel3");

                /*
                // Texture
                GL.GenTextures(1, out tex);
                GL.BindTexture(TextureTarget.Texture2D, tex);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);

                using (Bitmap bitmap = new Bitmap("../../texture.png"))
                {
                    BitmapData data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, data.Width, data.Height, 0, OpenTK.Graphics.OpenGL.PixelFormat.Bgra, PixelType.UnsignedByte, data.Scan0);
                    bitmap.UnlockBits(data);
                }
                */
            }

            void BindTexture(ref int id, TextureUnit unit, string name)
            {
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, id);
                GL.Uniform1(GL.GetUniformLocation(shaderProgram, name), unit - TextureUnit.Texture0);
            }

            void Render()
            {
                //FLogger.Log(LogType.Debug, "Render");

                if (shaderProgram == -1)
                    return;

                glControl.MakeCurrent();

                GL.Enable(EnableCap.CullFace);
                GL.FrontFace(FrontFaceDirection.Ccw);
                GL.CullFace(CullFaceMode.Back);
                GL.Clear(ClearBufferMask.ColorBufferBit);

                /*
                uniform vec3      iResolution;           // viewport resolution (in pixels)
                uniform float     iTime;           // shader playback time (in seconds)
                uniform float     iTimeDelta;            // render time (in seconds)
                uniform int       iFrame;                // shader playback frame
                uniform float     iChannelTime[4];       // channel playback time (in seconds)
                uniform vec3      iChannelResolution[4]; // channel resolution (in pixels)
                uniform vec4      iMouse;                // mouse pixel coords. xy: current (if MLB down), zw: click
                uniform samplerXX iChannel0..3;          // input channel. XX = 2D/Cube
                uniform vec4      iDate;                 // (year, month, day, time in seconds)
                uniform float     iSampleRate;           // sound sample rate (i.e., 44100)
                */

                GL.Uniform3(GL.GetUniformLocation(shaderProgram, "iResolution"), (float)ClientSize.Width, (float)ClientSize.Height, 0.0f);
                GL.Uniform1(GL.GetUniformLocation(shaderProgram, "iTime"), (float)FHDEHost.FrameTime);
                GL.Uniform1(GL.GetUniformLocation(shaderProgram, "iTimeDelta"), (float)(FHDEHost.FrameTime - frameTimeDelay));
                // store to next frame
                frameTimeDelay = FHDEHost.FrameTime;
                GL.Uniform1(GL.GetUniformLocation(shaderProgram, "iFrame"), frames++);
                GL.Uniform4(GL.GetUniformLocation(shaderProgram, "iMouse"), (float)dragX, (float)dragY, (float)clickX, (float)clickY);
                DateTime now = DateTime.Now;
                GL.Uniform4(GL.GetUniformLocation(shaderProgram, "iDate"), now.Year, now.Month, now.Day, now.TimeOfDay.TotalSeconds);

                /*
                for(int i=0; i<4; i++)
                {
                    if(tex[i] != -1)
                    {
                        GL.ActiveTexture(TextureUnit.Texture0);
                        GL.BindTexture(TextureTarget.Texture2D, tex[i]);
                        GL.Uniform1(GL.GetUniformLocation(shaderProgram, "iChannel" + i), 0);
                    }

                    //FLogger.Log(LogType.Debug, tex[i].ToString());
                }
                */

                /*
                shadertoy.setUniform4f("iMouse", draggedX, draggedY, clickX, clickY);
                shadertoy.setUniformTexture("iChannel0", color_noise, 1);
                shadertoy.setUniformTexture("iChannel1", gray_rock, 2);
                shadertoy.setUniformTexture("iChannel2", shell, 3);
                shadertoy.setUniformTexture("iChannel3", vulcanic_rock, 4);
                shadertoy.setUniform4f("iDate", ofGetYear(), ofGetMonth(), ofGetDay(), ofGetSeconds());
                */

                GL.Begin(OpenTK.Graphics.OpenGL.PrimitiveType.Quads);

                GL.Vertex3(-1.0f, 1.0f, 0.0f);
                GL.Vertex3(-1.0f, -1.0f, 0.0f);
                GL.Vertex3(1.0f, -1.0f, 0.0f);
                GL.Vertex3(1.0f, 1.0f, 0.0f);

                GL.End();

                glControl.SwapBuffers();

                if (this.width == glControl.Size.Width && this.height == glControl.Size.Height)
                {
                    GL.ReadPixels(0, 0, glControl.Size.Width, glControl.Size.Height,
                        OpenTK.Graphics.OpenGL.PixelFormat.Rgba, OpenTK.Graphics.OpenGL.PixelType.UnsignedByte, pixelBuffer);

                    this.invalidate = true;
                }
            }





            // ======================================================
            // dx9
            // ======================================================
            TextureResource<Info> CreateTextureResource()
            {
                //FLogger.Log(LogType.Debug, "CreateTextureResource()");

                this.width = glControl.Size.Width;
                this.height = glControl.Size.Height;

                pixelBuffer = new byte[this.width * this.height * 4];

                var info = new Info() { Width = this.width, Height = this.height };
                return TextureResource.Create(info, CreateTexture, UpdateTexture);
            }

            Texture CreateTexture(Info info, Device device)
            {
                //FLogger.Log(LogType.Debug, "CreateTexture()");

                return TextureUtils.CreateTexture(device, info.Width, info.Height);
            }

            unsafe void UpdateTexture(Info info, Texture texture)
            {
                //FLogger.Log(LogType.Debug, "UpdateTexture()");

                if (info.Width == this.width && info.Height == this.height)
                    TextureUtils.Fill32BitTexInPlace(texture, info, FillTexture);
            }

            //this is a pixelshader like method, which we pass to the fill function
            unsafe void FillTexture(uint* data, int row, int col, int width, int height, Info info)
            {
                // this method called per pixel
                // row(vertical) & col(horizonal) are each pixel's position and width & height are texture size
                // when 640x480, max row is 479 & max col is 639.

                // sometimes col or row over width & height depends on enviroment, so ignore that
                if (col >= info.Width || row >= info.Height)
                    return;

                // GL.getPixels results is flipped vertically, so flip by (info.Height - row - 1)
                int r = (col * 4) + ((info.Height - row - 1) * info.Width * 4); // 4 means R,G,B,A

                //a pixel is just a 32-bit unsigned int value
                uint pixel;

                // from BGRA to ARGB
                pixel = UInt32Utils.fromARGB(pixelBuffer[r + 3], pixelBuffer[r], pixelBuffer[r + 1], pixelBuffer[r + 2]);

                //copy pixel into texture
                TextureUtils.SetPtrVal2D(data, pixel, row, col, width);
            }
        }
    }
}
