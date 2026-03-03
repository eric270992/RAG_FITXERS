using System;
using System.Collections.Generic;
using System.Text;

namespace RAG_FITXERS.Models
{
    /// <summary>
    /// Representa un triplet de coneixement extret d'un chunk de text.
    /// Aquests triplets els generarà automaticament el model LLM a través de l'agent extractor de relacions.
    /// 
    /// Un triplet és la unitat mínima de coneixement, formada per:
    ///   ( Subject ) → ( Predicate ) → ( Object )
    ///   
    /// Exemple real:
    ///   Subject:   "Joan García (Director)"
    ///   Predicate: "director de"
    ///   Object:    "Departament de Logística"
    ///   
    /// Això permet connectar informació entre documents:
    ///   Joan García → director de  → Logística
    ///   Logística   → ha comprat   → Furgonetes
    ///   ─────────────────────────────────────────
    ///   Conclusió: Joan García ha comprat furgonetes (indirectament)
    /// </summary>
    public class RelationModel
    {
        public string Subject { get; set; } = "";
        public string Predicate { get; set; } = "";
        public string Object { get; set; } = "";
        public int Confidence { get; set; } = 0;
    }
}
