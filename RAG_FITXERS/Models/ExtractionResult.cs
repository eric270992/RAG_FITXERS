using System;
using System.Collections.Generic;
using System.Text;

namespace RAG_FITXERS.Models
{
    /// <summary>
    /// Resposta de l'agent extractor de coneixement (GraphService).
    /// 
    /// Quan el LLM analitza un chunk de text, retorna aquest objecte
    /// indicant si ha trobat relacions fiables o si cal descartar el chunk.
    /// 
    /// Flux de decisió de l'agent:
    ///   Text amb entitats clares   → mode="triplets" + llista de relacions
    ///   Text sense entitats clares → mode="discard"
    /// </summary>
    public class ExtractionResult
    {
        // "triplets" | "discard"
        public string Mode { get; set; } = "discard";

        // Només té contingut quan Mode = "triplets"
        /// Cada element representa una relació entre dues entitats del text.
        /// Els triplets amb Confidence inferior a MIN_CONFIDENCE (80) 
        /// seran filtrats i no es desaran a la base de dades.
        public List<RelationModel> Relations { get; set; } = new();
    }
}
