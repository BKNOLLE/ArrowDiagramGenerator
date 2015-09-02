﻿using ActivityDiagram.Generator.Model;
using ActivityDiagram.Contracts.Model.Activities;
using ActivityDiagram.Contracts.Model.Graph;
using QuickGraph;
using QuickGraph.Algorithms;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ActivityDiagram.Generator
{
    public class ActivityArrowGraphGenerator
    {
        private IEnumerable<ActivityDependency> activityDependencies;
        private Dictionary<int, Activity> activitiesDictionary;
        private Dictionary<Tuple<int, int>, int> edgesIdsMap;
        private Dictionary<string, int> verticeIdsMap;
        int edgesNextId = 0;
        int verticeNextId = 0;

        public ActivityArrowGraphGenerator(IEnumerable<ActivityDependency> activityDependencies)
        {
            this.activityDependencies = activityDependencies;
            this.activitiesDictionary = this.activityDependencies.ToDictionary(dep => dep.Activity.Id, dep => dep.Activity);
        }

        public ActivityArrowGraph GenerateGraph()
        {
            InitializeInternalStructures();

            var nodeGraph = CreateActivityNodeGraphFromProject();
            var reduction = nodeGraph.ComputeTransitiveReduction();
            var activityArrowDiagram = GenerateADGraph(reduction);
            RedirectADGraph(activityArrowDiagram);
            MergeADGraph(activityArrowDiagram);
            return CreateActivityArrowGraph(activityArrowDiagram);
        }

        private void InitializeInternalStructures()
        {
            edgesIdsMap = new Dictionary<Tuple<int, int>, int>();
            verticeIdsMap = new Dictionary<string, int>();
            edgesNextId = 0;
            verticeNextId = 0;
        }

        private BidirectionalGraph<int, SEdge<int>> CreateActivityNodeGraphFromProject()
        {
            return activityDependencies.
                SelectMany(act =>
                    act.Predecessors.Select(pred =>
                        new SEdge<int>(
                            pred, // Source
                            act.Activity.Id // Target
                            ))).ToBidirectionalGraph<int, SEdge<int>>();
        }

        private BidirectionalGraph<ADVertex, ADEdge> GenerateADGraph(BidirectionalGraph<int, SEdge<int>> nodeGraph)
        {
            var adGraph = new BidirectionalGraph<ADVertex, ADEdge>();

            // Go over all vertice - add them as a new activity edges.
            // activity vertex name are important for resuse when adding the edges.
            foreach (var vertex in nodeGraph.Vertices)
            {
                bool isCritical = activitiesDictionary[vertex].IsCritical;

                var startNode = ADVertex.New(vertex, ActivityVertexType.ActivityStart, isCritical);
                var endNode = ADVertex.New(vertex, ActivityVertexType.ActivityEnd, isCritical);
                adGraph.AddVertex(startNode);
                adGraph.AddVertex(endNode);

                ADEdge activityEdge = new ADEdge(startNode, endNode, vertex);

                adGraph.AddEdge(activityEdge);
            }

            // Go over all edges - convert them to dummy edges.
            // Make sure connections are maintained.
            foreach (var edge in nodeGraph.Edges)
            {
                bool isSourceCritical = activitiesDictionary[edge.Source].IsCritical;
                bool isTargetCritical = activitiesDictionary[edge.Target].IsCritical;

                ADEdge activityEdge = new ADEdge(
                    ADVertex.New(edge.Source, ActivityVertexType.ActivityEnd, isSourceCritical),
                    ADVertex.New(edge.Target, ActivityVertexType.ActivityStart, isTargetCritical));

                adGraph.AddEdge(activityEdge);
            }

            return adGraph;
        }

        private void RedirectADGraph(BidirectionalGraph<ADVertex, ADEdge> adGraph)
        {
            // Go over every vertex
            foreach (var pivotVertex in adGraph.Vertices)
            {
                // We only care at the moment about activity end vertice
                if (pivotVertex.Type == ActivityVertexType.ActivityEnd)
                {
                    // Get all the edges going out of this vertex
                    IEnumerable<ADEdge> foundOutEdges;
                    if (adGraph.TryGetOutEdges(pivotVertex, out foundOutEdges))
                    {
                        var commonDependenciesForAllTargets = new HashSet<ADVertex>();
                        // Find the common dependencies for all target vertice
                        foreach (var outEdge in foundOutEdges)
                        {
                            var target = outEdge.Target;

                            IEnumerable<ADEdge> dependenciesOfTarget;
                            if (adGraph.TryGetInEdges(target, out dependenciesOfTarget))
                            {
                                // Always work with dependencies which are dummies - since activities cannot/should not be redirected.
                                dependenciesOfTarget = dependenciesOfTarget.Where(dep => !dep.ActivityId.HasValue);

                                if (commonDependenciesForAllTargets.Count == 0)
                                {
                                    foreach (var dependency in dependenciesOfTarget)
                                    {
                                        commonDependenciesForAllTargets.Add(dependency.Source);
                                    }
                                }
                                else
                                {
                                    commonDependenciesForAllTargets.IntersectWith(dependenciesOfTarget.Select(d => d.Source).AsEnumerable());
                                }
                            }
                            // Else can never happen - the out edge for the current vertice is the in edge of the dependent
                            // so at least once exists.
                        }

                        // Now, if we have some common dependncies of all targets which are not the current vertex - they should be redirected
                        foreach (var commonDependency in commonDependenciesForAllTargets.Where(d => d != pivotVertex))
                        {
                            bool forceCritical = false;
                            IEnumerable<ADEdge> edgesOutOfDependency;
                            if (adGraph.TryGetOutEdges(commonDependency, out edgesOutOfDependency))
                            {
                                var depndents = foundOutEdges.Select(e => e.Target);

                                // This dependency should no longer point at the dependents of this vertex
                                var edgesToRemove = edgesOutOfDependency.Where(e => depndents.Contains(e.Target)).ToList();
                                foreach (var edgeToRemove in edgesToRemove)
                                {
                                    adGraph.RemoveEdge(edgeToRemove);

                                    forceCritical = forceCritical || edgeToRemove.IsCritical;
                                }
                            }
                            // Else should never happen

                            // This dependency should point at this vertex
                            var edgeToAdd = new ADEdge(commonDependency, pivotVertex, null, forceCritical);
                            adGraph.AddEdge(edgeToAdd);
                        }
                    }
                }
            }
        }

        private void MergeADGraph(BidirectionalGraph<ADVertex, ADEdge> adGraph)
        {
            bool dummyEdgeRemovedOnIteration = true;

            while (dummyEdgeRemovedOnIteration)
            {
                // Get all the current dummy edges in the graph
                var nonActivityEdges = adGraph.Edges.Where(e => !e.ActivityId.HasValue).ToList();

                foreach (var edge in nonActivityEdges)
                {
                    // Only remove one edge at a time - then, need to reevaluate the graph.
                    if (dummyEdgeRemovedOnIteration = TryRemoveDummyEdge(adGraph, edge)) break;

                }
            }
        }

        private bool TryRemoveDummyEdge(BidirectionalGraph<ADVertex, ADEdge> adGraph, ADEdge edge)
        {
            bool edgeRemoved = false;

            // If this is a single edge out or a single edge in - it adds no information to the graph and can be merged.
            var outDegree = adGraph.OutDegree(edge.Source);
            var inDegree = adGraph.InDegree(edge.Target);
            if (outDegree == 1 || inDegree == 1)
            {
                // Remove the vertex which has no other edges connected to it
                if (outDegree == 1 && inDegree != 1)
                {
                    IEnumerable<ADEdge> allIncoming;
                    if (!adGraph.TryGetInEdges(edge.Source, out allIncoming))
                    {
                        allIncoming = new List<ADEdge>();
                    }

                    bool abortMerge = false;

                    // Sanity check - don't make parallel edges (can have better huristic for this?)
                    foreach (var incomingEdge in allIncoming.ToList())
                    {
                        ADEdge dummy;
                        if (adGraph.TryGetEdge(incomingEdge.Source, edge.Target, out dummy))
                        {
                            abortMerge = true;
                        }
                    }

                    if (!abortMerge)
                    {
                        // Add the edges with the new source vertex
                        // And remove the old edges
                        foreach (var incomingEdge in allIncoming.ToList())
                        {
                            adGraph.AddEdge(new ADEdge(incomingEdge.Source, edge.Target, incomingEdge.ActivityId, incomingEdge.IsCritical));
                            adGraph.RemoveEdge(incomingEdge);
                        }

                        // Rmove the edge which is no longer needed
                        adGraph.RemoveEdge(edge);

                        // Now remove the vertex which was merged
                        adGraph.RemoveVertex(edge.Source);

                        edgeRemoved = true;
                    }
                }
                else
                {
                    IEnumerable<ADEdge> allOutgoing;
                    if (!adGraph.TryGetOutEdges(edge.Target, out allOutgoing))
                    {
                        allOutgoing = new List<ADEdge>();
                    }

                    bool abortMerge = false;

                    // Sanity check - don't make parallel edges (can have better huristic for this?)
                    foreach (var incomingEdge in allOutgoing.ToList())
                    {
                        ADEdge dummy;
                        if (adGraph.TryGetEdge(edge.Source, incomingEdge.Target, out dummy))
                        {
                            abortMerge = true;
                        }
                    }

                    if (!abortMerge)
                    {
                        // Add the edges with the new source vertex
                        // And remove the old edges
                        foreach (var outgoingEdge in allOutgoing.ToList())
                        {
                            adGraph.AddEdge(new ADEdge(edge.Source, outgoingEdge.Target, outgoingEdge.ActivityId, outgoingEdge.IsCritical));
                            adGraph.RemoveEdge(outgoingEdge);
                        }

                        // Rmove the edge which is no longer needed
                        adGraph.RemoveEdge(edge);

                        // Now remove the vertex which was merged
                        adGraph.RemoveVertex(edge.Target);

                        edgeRemoved = true;
                    }
                }
            }

            return edgeRemoved;
        }

        private ActivityArrowGraph CreateActivityArrowGraph(BidirectionalGraph<ADVertex, ADEdge> graph)
        {
            var activityArrowGraph = new ActivityArrowGraph();

            foreach (var edge in graph.Edges)
            {
                var sourceVertex = CreateVertexEvent(edge.Source, graph.InDegree(edge.Source), graph.OutDegree(edge.Source));
                var targetVertex = CreateVertexEvent(edge.Target, graph.InDegree(edge.Target), graph.OutDegree(edge.Target));

                Activity edgeActivity;

                TryGetActivity(edge, out edgeActivity);

                activityArrowGraph.AddEdge(CreateActivityEdge(sourceVertex, targetVertex, edgeActivity, edge.IsCritical));
            }

            return activityArrowGraph;
        }

        private ActivityEdge CreateActivityEdge(EventVertex source, EventVertex target, Activity edgeActivity, bool isCritical)
        {
            var edgeUniqueKey = new Tuple<int, int>(source.Id, target.Id);
            int activityEdgeId;
            if (!edgesIdsMap.TryGetValue(edgeUniqueKey, out activityEdgeId))
            {
                edgesIdsMap[edgeUniqueKey] = activityEdgeId = edgesNextId;
                edgesNextId++;
            }

            return new ActivityEdge(activityEdgeId, source, target, edgeActivity, isCritical);
        }

        private EventVertex CreateVertexEvent(ADVertex vertex, int inDegree, int outDegree)
        {
            int eventVertexId;
            if (!verticeIdsMap.TryGetValue(vertex.Id, out eventVertexId))
            {
                verticeIdsMap[vertex.Id] = eventVertexId = verticeNextId;
                verticeNextId++;
            }

            Activity activity;
            EventVertex eventVertex;
            if (vertex.Type == ActivityVertexType.Milestone && TryGetActivity(vertex, out activity))
            {
                eventVertex = EventVertex.CreateMilestone(eventVertexId, activity);
            }
            else if (inDegree == 0)
            {
                eventVertex = EventVertex.CreateGraphStart(eventVertexId);
            }
            else if (outDegree == 0)
            {
                eventVertex = EventVertex.CreateGraphEnd(eventVertexId);
            }
            else
            {
                eventVertex = EventVertex.Create(eventVertexId);
            }
            return eventVertex;
        }

        private bool TryGetActivity(ADEdge edge, out Activity activity)
        {
            activity = null;
            if (edge.ActivityId.HasValue && activitiesDictionary.ContainsKey(edge.ActivityId.Value))
            {
                activity = activitiesDictionary[edge.ActivityId.Value];
                return true;
            }

            return false;
        }

        private bool TryGetActivity(ADVertex vertex, out Activity activity)
        {
            activity = null;
            if (vertex.ActivityId.HasValue && activitiesDictionary.ContainsKey(vertex.ActivityId.Value))
            {
                activity = activitiesDictionary[vertex.ActivityId.Value];
                return true;
            }

            return false;
        }
    }
}
