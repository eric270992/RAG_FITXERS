using System;
using System.Collections.Generic;
using System.Text;

namespace RAG_FITXERS.Models
{
    public class JudgeResult
    {
        public int Score { get; set; }
        public string Reason { get; set; } = "";
        public bool NeedsMoreContext { get; set; } = false;
        public List<string> MissingEntities { get; set; } = new(); 
    }
}
