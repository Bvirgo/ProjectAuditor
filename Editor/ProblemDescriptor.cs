using System;

namespace Unity.ProjectAuditor.Editor
{
    [Serializable]
    public class ProblemDescriptor
    {
        public int id;
        public string opcode;
        public string type;
        public string method;
        public string value;
        public Func<bool> customEvaluator;
        public string area;
        public string problem;
        public string solution;
        public Rule.Action action;

        public string description
        {
            get
            {
                if (!string.IsNullOrEmpty(opcode))
                    return opcode;
                if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(method))
                    return string.Empty;
                return type + "." + method;
            }
        }
    }
}