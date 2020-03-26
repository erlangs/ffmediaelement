namespace Unosquare.FFME.Windows.Sample.Foundation
{
    using Common;
    using FFmpeg.AutoGen;
    using System;
    using System.Drawing;
    using System.IO;
    using System.Runtime.InteropServices;

    /// <inheritdoc />
    /// <summary>
    /// Provides an example of a very simple custom input stream.
    /// </summary>
    /// <seealso cref="IMediaInputStream" />
    public sealed unsafe class ForgeInputStream : IMediaInputStream
    {
        private readonly FileStream BackingStream;
        private readonly object ReadLock = new object();
        private readonly byte[] ReadBuffer;

        /// <summary>
        /// Initializes a new instance of the <see cref="ForgeInputStream"/> class.
        /// </summary>
        /// <param name="path">The path.</param>
        public ForgeInputStream(string path)
        {
            var fullPath = Path.GetFullPath(path);
            BackingStream = File.OpenRead(fullPath);
            var uri = new Uri(fullPath);
            StreamUri = new Uri(uri.ToString().ReplaceOrdinal("file://", Scheme));
            CanSeek = false;
            ReadBuffer = new byte[ReadBufferLength];
        }

        /// <summary>
        /// The custom file scheme (URL prefix) including the :// sequence.
        /// </summary>
        public static string Scheme => "forge://";

        /// <inheritdoc />
        public Uri StreamUri { get; }

        /// <inheritdoc />
        public bool CanSeek { get; }

        /// <inheritdoc />
        public int ReadBufferLength => 1024 * 16;

        /// <inheritdoc />
        public InputStreamInitializing OnInitializing { get; }

        /// <inheritdoc />
        public InputStreamInitialized OnInitialized { get; }

        /// <inheritdoc />
        public void Dispose()
        {
            BackingStream?.Dispose();
        }

        /// <summary>
        /// Reads from the underlying stream and writes up to <paramref name="targetBufferLength" /> bytes
        /// to the <paramref name="targetBuffer" />. Returns the number of bytes that were written.
        /// </summary>
        /// <param name="opaque">The opaque.</param>
        /// <param name="targetBuffer">The target buffer.</param>
        /// <param name="targetBufferLength">Length of the target buffer.</param>
        /// <returns>
        /// The number of bytes that have been read.
        /// </returns>
        public int Read(void* opaque, byte* targetBuffer, int targetBufferLength)
        {
            lock (ReadLock)
            {
                try
                {
                    if (_stream == null || _stream.Length == 0) {
                        H264VideoStreamEncoder();
                        _stream.Seek(0, SeekOrigin.Begin);
                    }
                    var readCount = _stream.Read(ReadBuffer, 0, ReadBuffer.Length);
                    //var readCount = BackingStream.Read(ReadBuffer, 0, ReadBuffer.Length);
                    if (readCount > 0)
                        Marshal.Copy(ReadBuffer, 0, (IntPtr)targetBuffer, readCount);

                    return readCount;
                }
                catch (Exception)
                {
                    return ffmpeg.AVERROR_EOF;
                }
            }
        }

        /// <inheritdoc />
        public long Seek(void* opaque, long offset, int whence)
        {
            lock (ReadLock)
            {
                try
                {
                    return whence == ffmpeg.AVSEEK_SIZE ?
                        BackingStream.Length : BackingStream.Seek(offset, SeekOrigin.Begin);
                }
                catch
                {
                    return ffmpeg.AVERROR_EOF;
                }
            }
        }

        private Size _frameSize;
        private int _linesizeU;
        private int _linesizeV;
        private int _linesizeY;
        private AVCodec* _pCodec;
        private AVCodecContext* _pCodecContext;
        private Stream _stream;
        private int _uSize;
        private int _ySize;

        void H264VideoStreamEncoder()
        {
            int fps = 25;
            Size frameSize = new Size(1920, 1080);
            _stream = new MemoryStream();
            // _stream = stream;
            _frameSize = frameSize;

            //var codecId = AVCodecID.AV_CODEC_ID_H264;
            var codecId = AVCodecID.AV_CODEC_ID_MJPEG;

            _pCodec = ffmpeg.avcodec_find_encoder(codecId);
            if (_pCodec == null) throw new InvalidOperationException("Codec not found.");

            _pCodecContext = ffmpeg.avcodec_alloc_context3(_pCodec);
            _pCodecContext->width = frameSize.Width;
            _pCodecContext->height = frameSize.Height;
            _pCodecContext->time_base = new AVRational { num = 1, den = fps };
            //_pCodecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            _pCodecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUVJ420P;
            ffmpeg.av_opt_set(_pCodecContext->priv_data, "preset", "veryslow", 0);

            ffmpeg.avcodec_open2(_pCodecContext, _pCodec, null).ThrowExceptionIfError();

            _linesizeY = frameSize.Width;
            _linesizeU = frameSize.Width / 2;
            _linesizeV = frameSize.Width / 2;

            _ySize = _linesizeY * frameSize.Height;
            _uSize = _linesizeU * frameSize.Height / 2;

            EncodeImagesToH264();

            ffmpeg.avcodec_close(_pCodecContext);
            ffmpeg.av_free(_pCodecContext);
            ffmpeg.av_free(_pCodec);
        }

        private unsafe void EncodeImagesToH264()
        {
            var outputFileName = "out.h264";
            var fps = 25;
            var sourceSize = new Size(1920, 1080);
            var sourcePixelFormat = AVPixelFormat.AV_PIX_FMT_BGR24;
            var destinationSize = sourceSize;
            //var destinationPixelFormat = AVPixelFormat.AV_PIX_FMT_YUV420P;
            var destinationPixelFormat = AVPixelFormat.AV_PIX_FMT_YUVJ420P;

            byte[] bitmapData = new byte[6220800];

            //using (var frameImage = Image.FromFile(frameFile))
            //using (var frameBitmap = frameImage is Bitmap bitmap ? bitmap : new Bitmap(frameImage))
            //{
            //    bitmapData = GetBitmapData(frameBitmap);
            //}

            int frameNumber = 0;
            fixed (byte* pBitmapData = bitmapData)
            {
                var data = new byte_ptrArray8 { [0] = pBitmapData };
                var linesize = new int_array8 { 
                    [0] = _linesizeY,
                    [1] = _linesizeU,
                    [2] = _linesizeV,
                };
                var frame = new AVFrame
                {
                    data = data,
                    linesize = linesize,
                    height = sourceSize.Height,
                    width = sourceSize.Width
                };
                frame.pts = frameNumber * fps;
                Encode(frame);
            }

        }

        public void Encode(AVFrame frame)
        {
            //if (frame.format != (int) _pCodecContext->pix_fmt) throw new ArgumentException("Invalid pixel format.", nameof(frame));
            if (frame.width != _frameSize.Width) throw new ArgumentException("Invalid width.", nameof(frame));
            if (frame.height != _frameSize.Height) throw new ArgumentException("Invalid height.", nameof(frame));
            if (frame.linesize[0] != _linesizeY) throw new ArgumentException("Invalid Y linesize.", nameof(frame));
            if (frame.linesize[1] != _linesizeU) throw new ArgumentException("Invalid U linesize.", nameof(frame));
            if (frame.linesize[2] != _linesizeV) throw new ArgumentException("Invalid V linesize.", nameof(frame));
            frame.data[1] = frame.data[0] + _ySize;
            if (frame.data[1] - frame.data[0] != _ySize) throw new ArgumentException("Invalid Y data size.", nameof(frame));
            frame.data[2] = frame.data[1] + _uSize;
            if (frame.data[2] - frame.data[1] != _uSize) throw new ArgumentException("Invalid U data size.", nameof(frame));

            var pPacket = ffmpeg.av_packet_alloc();
            try
            {
                int error;
                do
                {
                    ffmpeg.avcodec_send_frame(_pCodecContext, &frame).ThrowExceptionIfError();

                    error = ffmpeg.avcodec_receive_packet(_pCodecContext, pPacket);
                } while (error == ffmpeg.AVERROR(ffmpeg.EAGAIN));

                error.ThrowExceptionIfError();

                using (var packetStream = new UnmanagedMemoryStream(pPacket->data, pPacket->size)) packetStream.CopyTo(_stream);
            }
            finally
            {
                ffmpeg.av_packet_unref(pPacket);
            }
        }

    }

    internal static class FFmpegHelper
    {
        public static unsafe string av_strerror(int error)
        {
            var bufferSize = 1024;
            var buffer = stackalloc byte[bufferSize];
            ffmpeg.av_strerror(error, buffer, (ulong)bufferSize);
            var message = Marshal.PtrToStringAnsi((IntPtr)buffer);
            return message;
        }

        public static int ThrowExceptionIfError(this int error)
        {
            if (error < 0) throw new ApplicationException(av_strerror(error));
            return error;
        }
    }
}
