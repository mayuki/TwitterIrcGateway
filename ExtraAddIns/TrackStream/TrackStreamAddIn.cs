﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.ServiceModel.Dispatcher;
using System.Text;
using System.Threading;
using Misuzilla.Applications.TwitterIrcGateway.AddIns.Console;
using System.Runtime.Serialization.Json;

namespace Misuzilla.Applications.TwitterIrcGateway.AddIns.TrackStream
{
    public class TrackStreamAddIn : AddInBase
    {
        private HashSet<Int64> _friendIds;

        private Thread _workerThread;
        private Boolean _isRunning;
        private HttpWebRequest _webRequest;

        private String _trackString = "";

        public TrackStreamConfig Config { get; set; }

        public override void Initialize()
        {
            Session.AddInsLoadCompleted += (sender, e) =>
            {
                Session.AddInManager.GetAddIn<ConsoleAddIn>().RegisterContext<TrackStreamContext>();
                Config = Session.AddInManager.GetConfig<TrackStreamConfig>();
                Setup(Config.Enabled);
            };
        }
        public override void Uninitialize()
        {
            Setup(false);
        }

        internal void Setup(Boolean isStart)
        {
            if (_workerThread != null)
            {
                _isRunning = false;

                if (_webRequest != null)
                {
                    _webRequest.Abort();
                    _webRequest = null;
                }

                _workerThread.Abort();
                _workerThread.Join(200);
                _workerThread = null;
            }

            if (isStart)
            {
                _friendIds = new HashSet<Int64>();
                _workerThread = new Thread(WorkerProcedure);
                _workerThread.Start();
                _isRunning = true;
            }
        }

        private void WorkerProcedure()
        {
            try
            {

                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(_Status));
                DataContractJsonSerializer serializer2 = new DataContractJsonSerializer(typeof(_FriendsObject));
                DataContractJsonSerializer serializer3 = new DataContractJsonSerializer(typeof(_EventObject));

                //_webRequest = WebRequest.Create("http://stream.twitter.com/1/statuses/filter.json?track=" + Config.TrackString) as HttpWebRequest;
                //_webRequest.Credentials = new NetworkCredential(CurrentSession.Connections[0].UserInfo.UserName,
                //                                                CurrentSession.Connections[0].UserInfo.Password);
                //_webRequest.PreAuthenticate = true;
                _webRequest = CurrentSession.TwitterService.OAuthClient.CreateRequest(
                                                                       new Uri("http://stream.twitter.com/1/statuses/filter.json?track=" + Config.TrackString),
                                                                       TwitterOAuth.HttpMethod.GET); 
                _webRequest.ServicePoint.ConnectionLimit = 1000;
                using (var response = _webRequest.GetResponse())
                using (var stream = response.GetResponseStream())
                {
                    stream.ReadTimeout = 30 * 1000;

                    StreamReader sr = new StreamReader(stream, Encoding.UTF8);
                    Boolean isFirstLine = true;
                    while (!sr.EndOfStream && _isRunning)
                    {
                        var line = sr.ReadLine();
                        if (String.IsNullOrEmpty(line))
                            continue;

                        _Status statusJson = null;
                        try
                        {
                            // XXX: これはてぬき
                            if (isFirstLine)
                            {
                                isFirstLine = false;
                                _FriendsObject streamObject =
                                    serializer2.ReadObject(new MemoryStream(Encoding.UTF8.GetBytes(line))) as
                                    _FriendsObject;
                                if (streamObject != null && streamObject.friends != null)
                                {
                                    _friendIds.UnionWith(streamObject.friends);
                                }
                            }
                            else if (line.IndexOf("\"event\":") > -1)
                            {
                                _EventObject eventObj =
                                    serializer3.ReadObject(new MemoryStream(Encoding.UTF8.GetBytes(line))) as _EventObject;

                                if (eventObj.Event == "follow" && eventObj.source.id == CurrentSession.TwitterUser.Id)
                                    _friendIds.Add(eventObj.target.id);
                            }
                            else
                            {
                                statusJson =
                                    serializer.ReadObject(new MemoryStream(Encoding.UTF8.GetBytes(line))) as _Status;
                            }

                        }
                        catch
                        {
                            //CurrentSession.SendServerErrorMessage("TrackStream(Deserialize): " + line);
                            continue;
                        }

                        if (statusJson == null || statusJson.id == 0)
                            continue;

                        Status status = statusJson.ToStatus();
                        Boolean friendCheckRequired = false;
                        if (Config.AllAtMode ||
                            (statusJson.in_reply_to_user_id.HasValue == false) ||
                            (statusJson.in_reply_to_user_id.HasValue && _friendIds.Contains(statusJson.in_reply_to_user_id.Value)))
                        {
                            CurrentSession.TwitterService.ProcessStatus(status,
                                                                        (s) =>
                                                                        CurrentSession.ProcessTimelineStatus(s,
                                                                                                             ref friendCheckRequired,
                                                                                                             false,
                                                                                                             false));
                        }
                    }
                }
            }
            catch (ThreadAbortException)
            { }
            catch (Exception e)
            {
                CurrentSession.SendServerErrorMessage("TrackStream: " + e.ToString());
                CurrentSession.SendServerErrorMessage("http://stream.twitter.com/1/statuses/filter.json?track=" + Config.TrackString);
            }
            finally
            {
                _isRunning = false;
            }
        }
    }

    [Description("Track Stream設定コンテキストに切り替えます")]
    public class TrackStreamContext : Context
    {
        public override IConfiguration[] Configurations
        {
            get
            {
                return new[] { CurrentSession.AddInManager.GetAddIn<TrackStreamAddIn>().Config };
            }
        }

        [Description("Track Stream を有効にします")]
        public void Enable()
        {
            var config = CurrentSession.AddInManager.GetConfig<TrackStreamConfig>();
            config.Enabled = true;
            CurrentSession.AddInManager.SaveConfig(config);
            CurrentSession.AddInManager.GetAddIn<TrackStreamAddIn>().Setup(config.Enabled);
            Console.NotifyMessage("Track Stream を有効にしました。");
        }
        [Description("Track Stream を無効にします")]
        public void Disable()
        {
            var config = CurrentSession.AddInManager.GetConfig<TrackStreamConfig>();
            config.Enabled = false;
            CurrentSession.AddInManager.SaveConfig(config);
            CurrentSession.AddInManager.GetAddIn<TrackStreamAddIn>().Setup(config.Enabled);
            Console.NotifyMessage("Track Stream を無効にしました。");
        }
    }

    public class TrackStreamConfig : IConfiguration
    {
        [Browsable(false)]
        public Boolean Enabled { get; set; }

        [Description("all@と同じ挙動になるかどうかを指定します。")]
        public Boolean AllAtMode { get; set; }

        [Description("hogehoge")]
        public String TrackString { get; set; }

        //        [Description("切断された際に自動的に再接続を試みるかどうかを指定します。")]
        //        public Boolean AutoRestart { get; set; }
    }

    [DataContract]
    class _Status
    {
        [DataMember]
        public Int64 id { get; set; }
        [DataMember]
        public String text { get; set; }
        [DataMember]
        public String created_at { get; set; }
        [DataMember]
        public String source { get; set; }
        [DataMember]
        public _User user { get; set; }

        public DateTime CreatedAt { get { return DateTime.ParseExact(created_at, "ddd MMM dd HH:mm:ss zz00 yyyy", CultureInfo.InvariantCulture.DateTimeFormat); } }

        [DataMember]
        public Int64? in_reply_to { get; set; }

        [DataMember]
        public Int64? in_reply_to_user_id { get; set; }

        [DataMember]
        public _Status retweeted_status { get; set; }

        public Status ToStatus()
        {
            return new Status()
            {
                CreatedAt = this.CreatedAt,
                Text = this.text,
                Source = this.source,
                Id = this.id,
                InReplyToUserId =
                    this.in_reply_to_user_id.HasValue
                        ? this.in_reply_to_user_id.Value.ToString()
                        : null,
                InReplyToStatusId =
                    this.in_reply_to.HasValue
                        ? this.in_reply_to.Value.ToString()
                        : null,
                RetweetedStatus = (this.retweeted_status == null) ? null : this.retweeted_status.ToStatus(),
                User = this.user.ToUser()
            };
        }
    }

    [DataContract]
    class _EventTarget
    {
        [DataMember]
        public Int64 id { get; set; }
    }

    [DataContract]
    class _User
    {
        [DataMember]
        public Int64 id { get; set; }
        [DataMember]
        public String screen_name { get; set; }
        [DataMember]
        public String profile_image_url { get; set; }
        [DataMember(Name = "protected")]
        public Boolean Protected { get; set; }

        public User ToUser()
        {
            return new User()
            {
                Id = (Int32)this.id,
                Protected = this.Protected,
                ProfileImageUrl = this.profile_image_url,
                ScreenName = this.screen_name
            };
        }
    }


    [DataContract]
    class _FriendsObject
    {
        [DataMember]
        public List<Int64> friends { get; set; }
    }

    [DataContract]
    class _EventObject
    {
        [DataMember(Name = "event")]
        public String Event { get; set; }
        [DataMember]
        public _EventTarget target { get; set; }
        [DataMember]
        public _EventTarget source { get; set; }
    }
}

