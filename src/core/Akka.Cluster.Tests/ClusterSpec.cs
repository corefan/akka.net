﻿//-----------------------------------------------------------------------
// <copyright file="ClusterSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2016 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2016 Akka.NET project <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Immutable;
using System.Linq;
using Akka.Actor;
using Akka.Configuration;
using Akka.TestKit;
using Akka.Util.Internal;
using Xunit;
using FluentAssertions;

namespace Akka.Cluster.Tests
{
    public class ClusterSpec : AkkaSpec
    {
        const string Config = @"    
            akka.cluster {
              auto-down-unreachable-after = 0s
              periodic-tasks-initial-delay = 120 s
              publish-stats-interval = 0 s # always, when it happens
              run-coordinated-shutdown-when-down = off
            }
            akka.actor.provider = ""Akka.Cluster.ClusterActorRefProvider, Akka.Cluster""
            akka.coordinated-shutdown.terminate-actor-system = off
            akka.remote.log-remote-lifecycle-events = off
            akka.remote.dot-netty.tcp.port = 0";

        public IActorRef Self { get { return TestActor; } }

        readonly Address _selfAddress;
        readonly Cluster _cluster;

        internal ClusterReadView ClusterView { get { return _cluster.ReadView; } }

        public ClusterSpec()
            : base(Config)
        {
            _selfAddress = Sys.AsInstanceOf<ExtendedActorSystem>().Provider.DefaultAddress;
            _cluster = Cluster.Get(Sys);
        }

        public void LeaderActions()
        {
            _cluster.ClusterCore.Tell(InternalClusterAction.LeaderActionsTick.Instance);
        }

        [Fact]
        public void A_cluster_must_use_the_address_of_the_remote_transport()
        {
            _cluster.SelfAddress.Should().Be(_selfAddress);
        }

        [Fact]
        public void A_cluster_must_initially_become_singleton_cluster_when_joining_itself_and_reach_convergence()
        {
            ClusterView.Members.Count.Should().Be(0);
            _cluster.Join(_selfAddress);
            LeaderActions(); // Joining -> Up
            AwaitCondition(() => ClusterView.IsSingletonCluster);
            ClusterView.Self.Address.Should().Be(_selfAddress);
            ClusterView.Members.Select(m => m.Address).ToImmutableHashSet()
                .Should().BeEquivalentTo(ImmutableHashSet.Create(_selfAddress));
            AwaitAssert(() => ClusterView.Status.Should().Be(MemberStatus.Up));
        }

        [Fact]
        public void A_cluster_must_publish_initial_state_as_snapshot_to_subscribers()
        {
            try
            {
                _cluster.Subscribe(TestActor, ClusterEvent.InitialStateAsSnapshot, new[] { typeof(ClusterEvent.IMemberEvent) });
                ExpectMsg<ClusterEvent.CurrentClusterState>();
            }
            finally
            {
                _cluster.Unsubscribe(TestActor);
            }
        }

        [Fact]
        public void A_cluster_must_publish_initial_state_as_events_to_subscribers()
        {
            try
            {
                // TODO: this should be removed
                _cluster.Join(_selfAddress);
                LeaderActions(); // Joining -> Up

                _cluster.Subscribe(TestActor, ClusterEvent.InitialStateAsEvents, new[] { typeof(ClusterEvent.IMemberEvent) });
                ExpectMsg<ClusterEvent.MemberUp>();
            }
            finally
            {
                _cluster.Unsubscribe(TestActor);
            }
        }

        [Fact]
        public void A_cluster_must_send_current_cluster_state_to_one_receiver_when_requested()
        {
            _cluster.SendCurrentClusterState(TestActor);
            ExpectMsg<ClusterEvent.CurrentClusterState>();
        }

        // this should be the last test step, since the cluster is shutdown
        [Fact]
        public void A_cluster_must_publish_member_removed_when_shutdown()
        {
            // TODO: this should be removed
            _cluster.Join(_selfAddress);
            LeaderActions(); // Joining -> Up

            var callbackProbe = CreateTestProbe();
            _cluster.RegisterOnMemberRemoved(() =>
            {
                callbackProbe.Tell("OnMemberRemoved");
            });

            _cluster.Subscribe(TestActor, new[] { typeof(ClusterEvent.MemberRemoved) });
            // first, is in response to the subscription
            ExpectMsg<ClusterEvent.CurrentClusterState>();

            _cluster.Shutdown();
            ExpectMsg<ClusterEvent.MemberRemoved>().Member.Address.Should().Be(_selfAddress);

            callbackProbe.ExpectMsg("OnMemberRemoved");
        }

        /// <summary>
        /// https://github.com/akkadotnet/akka.net/issues/2442
        /// </summary>
        [Fact]
        public void BugFix_2442_RegisterOnMemberUp_should_fire_if_node_already_up()
        {
            // TODO: this should be removed
            _cluster.Join(_selfAddress);
            LeaderActions(); // Joining -> Up

            // Member should already be up
            _cluster.Subscribe(TestActor, ClusterEvent.InitialStateAsEvents, new[] { typeof(ClusterEvent.IMemberEvent) });
            ExpectMsg<ClusterEvent.MemberUp>();

            var callbackProbe = CreateTestProbe();
            _cluster.RegisterOnMemberUp(() =>
            {
                callbackProbe.Tell("RegisterOnMemberUp");
            });
            callbackProbe.ExpectMsg("RegisterOnMemberUp");
        }

        [Fact]
        public void A_cluster_must_complete_LeaveAsync_task_upon_being_removed()
        {
            var sys2 = ActorSystem.Create("ClusterSpec2", ConfigurationFactory.ParseString(@"
                akka.actor.provider = ""cluster""
                akka.remote.dot-netty.tcp.port = 0
                akka.coordinated-shutdown.run-by-clr-shutdown-hook = off
                akka.coordinated-shutdown.terminate-actor-system = off
                akka.cluster.run-coordinated-shutdown-when-down = off
            ").WithFallback(Akka.TestKit.Configs.TestConfigs.DefaultConfig));

            var probe = CreateTestProbe(sys2);
            Cluster.Get(sys2).Subscribe(probe.Ref, typeof(ClusterEvent.IMemberEvent));
            probe.ExpectMsg<ClusterEvent.CurrentClusterState>();

            Cluster.Get(sys2).Join(Cluster.Get(sys2).SelfAddress);
            probe.ExpectMsg<ClusterEvent.MemberUp>();

            var leaveTask = Cluster.Get(sys2).LeaveAsync();

            leaveTask.IsCompleted.Should().BeFalse();
            probe.ExpectMsg<ClusterEvent.MemberLeft>();
            probe.ExpectMsg<ClusterEvent.MemberExited>();
            probe.ExpectMsg<ClusterEvent.MemberRemoved>();

            AwaitCondition(() => leaveTask.IsCompleted);

            // A second call for LeaveAsync should complete immediately
            Cluster.Get(sys2).LeaveAsync().IsCompleted.Should().BeTrue();
        }

        [Fact]
        public void A_cluster_must_be_allowed_to_join_and_leave_with_local_address()
        {
            var sys2 = ActorSystem.Create("ClusterSpec2", ConfigurationFactory.ParseString(@"akka.actor.provider = ""Akka.Cluster.ClusterActorRefProvider, Akka.Cluster""
        akka.remote.dot-netty.tcp.port = 0"));

            try
            {
                var @ref = sys2.ActorOf(Props.Empty);
                Cluster.Get(sys2).Join(@ref.Path.Address); // address doesn't contain full address information
                Within(5.Seconds(), () =>
                {
                    AwaitAssert(() =>
                    {
                        Cluster.Get(sys2).State.Members.Count.Should().Be(1);
                        Cluster.Get(sys2).State.Members.First().Status.Should().Be(MemberStatus.Up);
                    });
                });

                Cluster.Get(sys2).Leave(@ref.Path.Address);

                Within(5.Seconds(), () =>
                {
                    AwaitAssert(() =>
                    {
                        Cluster.Get(sys2).IsTerminated.Should().BeTrue();
                    });
                });
            }
            finally
            {
                Shutdown(sys2);
            }
        }

        [Fact]
        public void A_cluster_must_allow_to_resolve_RemotePathOf_any_actor()
        {
            var remotePath = _cluster.RemotePathOf(TestActor);

            TestActor.Path.Address.Host.Should().BeNull();
            _cluster.RemotePathOf(TestActor).Uid.Should().Be(TestActor.Path.Uid);
            _cluster.RemotePathOf(TestActor).Address.Should().Be(_selfAddress);
        }

        [Fact]
        public void A_cluster_must_leave_via_CoordinatedShutdownRun()
        {
            var sys2 = ActorSystem.Create("ClusterSpec2", ConfigurationFactory.ParseString(@"
                akka.actor.provider = ""cluster""
                akka.remote.dot-netty.tcp.port = 0
                akka.coordinated-shutdown.run-by-clr-shutdown-hook = off
                akka.coordinated-shutdown.terminate-actor-system = off
                akka.cluster.run-coordinated-shutdown-when-down = off
            ").WithFallback(Akka.TestKit.Configs.TestConfigs.DefaultConfig));

            try
            {
                var probe = CreateTestProbe(sys2);
                Cluster.Get(sys2).Subscribe(probe.Ref, typeof(ClusterEvent.IMemberEvent));
                probe.ExpectMsg<ClusterEvent.CurrentClusterState>();
                Cluster.Get(sys2).Join(Cluster.Get(sys2).SelfAddress);
                probe.ExpectMsg<ClusterEvent.MemberUp>();

                CoordinatedShutdown.Get(sys2).Run();

                probe.ExpectMsg<ClusterEvent.MemberLeft>();
                probe.ExpectMsg<ClusterEvent.MemberExited>();
                probe.ExpectMsg<ClusterEvent.MemberRemoved>();
            }
            finally
            {
                Shutdown(sys2);
            }
        }

        [Fact]
        public void A_cluster_must_terminate_ActorSystem_via_leave_CoordinatedShutdown()
        {
            var sys2 = ActorSystem.Create("ClusterSpec2", ConfigurationFactory.ParseString(@"
                akka.actor.provider = ""cluster""
                akka.remote.dot-netty.tcp.port = 0
                akka.coordinated-shutdown.terminate-actor-system = on
            ").WithFallback(Akka.TestKit.Configs.TestConfigs.DefaultConfig));

            try
            {
                var probe = CreateTestProbe(sys2);
                Cluster.Get(sys2).Subscribe(probe.Ref, typeof(ClusterEvent.IMemberEvent));
                probe.ExpectMsg<ClusterEvent.CurrentClusterState>();
                Cluster.Get(sys2).Join(Cluster.Get(sys2).SelfAddress);
                probe.ExpectMsg<ClusterEvent.MemberUp>();

                Cluster.Get(sys2).Leave(Cluster.Get(sys2).SelfAddress);

                probe.ExpectMsg<ClusterEvent.MemberLeft>();
                probe.ExpectMsg<ClusterEvent.MemberExited>();
                probe.ExpectMsg<ClusterEvent.MemberRemoved>(); 
                AwaitCondition(() => sys2.WhenTerminated.IsCompleted, TimeSpan.FromSeconds(10));
                Cluster.Get(sys2).IsTerminated.Should().BeTrue();
            }
            finally
            {
                Shutdown(sys2);
            }
        }

        [Fact]
        public void A_cluster_must_terminate_ActorSystem_via_Down_CoordinatedShutdown()
        {
            var sys3 = ActorSystem.Create("ClusterSpec3", ConfigurationFactory.ParseString(@"
                akka.actor.provider = ""cluster""
                akka.remote.dot-netty.tcp.port = 0
                akka.coordinated-shutdown.terminate-actor-system = on
                akka.cluster.run-coordinated-shutdown-when-down = on
                akka.loglevel=DEBUG
            ").WithFallback(Akka.TestKit.Configs.TestConfigs.DefaultConfig));

            try
            {
                var probe = CreateTestProbe(sys3);
                Cluster.Get(sys3).Subscribe(probe.Ref, typeof(ClusterEvent.IMemberEvent));
                probe.ExpectMsg<ClusterEvent.CurrentClusterState>();
                Cluster.Get(sys3).Join(Cluster.Get(sys3).SelfAddress);
                probe.ExpectMsg<ClusterEvent.MemberUp>();

                Cluster.Get(sys3).Down(Cluster.Get(sys3).SelfAddress);

                probe.ExpectMsg<ClusterEvent.MemberRemoved>();
                AwaitCondition(() => sys3.WhenTerminated.IsCompleted, TimeSpan.FromSeconds(10));
                Cluster.Get(sys3).IsTerminated.Should().BeTrue();
            }
            finally
            {
                Shutdown(sys3);
            }
        }
    }
}

