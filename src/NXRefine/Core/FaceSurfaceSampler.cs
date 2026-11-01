using System;
using System.Linq;
using NXOpen;
using NXOpen.UF;

namespace NXRefine.Core
{
    // Keeps the fitting grid unchanged while avoiding per-point curvature
    // evaluation and managed/native calls. The evaluator never leaves NX's thread.
    internal sealed class FaceSurfaceSampler : IDisposable
    {
        internal sealed class Counters
        {
            internal int BatchCalls;
            internal int BatchPoints;
            internal int FallbackFaces;
        }

        private readonly NxContext context;
        private readonly Face face;
        private readonly Counters counters;
        private IntPtr evaluator;
        private double normalSign;
        private bool legacy;

        internal FaceSurfaceSampler(NxContext context, Face face, Counters counters)
        { this.context=context; this.face=face; this.counters=counters; }

        internal void Evaluate(double[][] parameters, out double[][] points, out double[][] normals)
        {
            SelectionScan.Checkpoint();
            if (!legacy && TryEvaluate(parameters,out points,out normals)) return;
            if (!legacy)
            {
                legacy=true;
                if(counters!=null)counters.FallbackFaces++;
            }
            points=new double[parameters.Length][];
            normals=new double[parameters.Length][];
            for(int i=0;i<parameters.Length;i++)
            {
                SelectionScan.Checkpoint();
                points[i]=new double[3];normals[i]=new double[3];
                AskProperties(parameters[i],points[i],normals[i]);
            }
        }

        private bool TryEvaluate(double[][] parameters,out double[][] points,out double[][] normals)
        {
            points=null;normals=null;
            try
            {
                // Initialize2 preserves face UV mapping for trimmed B-surfaces
                // and blends, unlike the older evaluator initialization API.
                if(evaluator==IntPtr.Zero)context.UF.Evalsf.Initialize2(face.Tag,out evaluator);
                var values=new ModlSrfValue[parameters.Length];
                context.UF.Evalsf.EvaluateArray(evaluator,UFConstants.UF_MODL_EVAL_UNIT_NORMAL,
                    parameters.Length,parameters.SelectMany(uv=>uv).ToArray(),values);
                foreach(ModlSrfValue value in values)
                {
                    if(!Finite(value.srf_pos)||!Finite(value.srf_unormal)||!Finite(value.srf_du)||!Finite(value.srf_dv))return false;
                    double du=value.srf_du.Sum(x=>x*x),dv=value.srf_dv.Sum(x=>x*x);
                    double dot=Enumerable.Range(0,3).Sum(i=>value.srf_du[i]*value.srf_dv[i]);
                    // At poles Evalsf may return a zero normal where AskFaceProps
                    // supplies the limiting face normal. Preserve that behavior.
                    if(du<1e-24||dv<1e-24||du*dv-dot*dot<=1e-20*du*dv || value.srf_unormal.Sum(x=>x*x)<1e-18)return false;
                }
                if(normalSign==0)
                {
                    var referencePoint=new double[3];var referenceNormal=new double[3];
                    AskProperties(parameters[0],referencePoint,referenceNormal);
                    normalSign=Enumerable.Range(0,3).Sum(i=>referenceNormal[i]*values[0].srf_unormal[i])<0?-1:1;
                    if(Enumerable.Range(0,3).Sum(i=>Math.Pow(referencePoint[i]-values[0].srf_pos[i],2))>1e-14 ||
                        Enumerable.Range(0,3).Sum(i=>Math.Pow(referenceNormal[i]-normalSign*values[0].srf_unormal[i],2))>1e-14)return false;
                }
                points=values.Select(value=>value.srf_pos).ToArray();
                normals=values.Select(value=>value.srf_unormal.Select(x=>normalSign*x).ToArray()).ToArray();
                if(counters!=null){counters.BatchCalls++;counters.BatchPoints+=values.Length;}
                return true;
            }
            catch(NXException){return false;}
            catch(ArgumentException){return false;}
        }

        private static bool Finite(double[] values)
        { return values!=null&&values.Length==3&&values.All(x=>!double.IsNaN(x)&&!double.IsInfinity(x)); }

        private void AskProperties(double[] uv,double[] point,double[] normal)
        {
            context.UF.Modl.AskFaceProps(face.Tag,uv,point,new double[3],new double[3],
                new double[3],new double[3],normal,new double[2]);
        }

        public void Dispose()
        {
            if(evaluator==IntPtr.Zero)return;
            try{context.UF.Evalsf.Free(out evaluator);}
            catch(NXException ex){context.Log("Fill Holes surface evaluator cleanup: "+ex.Message);}
        }
    }
}
