using Microsoft.SemanticKernel;
using RAG_FITXERS.Utils;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;

namespace RAG_FITXERS.Plugins
{
    public class RagPlugin
    {
        private readonly RagOrchestrator _orchestrator;

        public RagPlugin(RagOrchestrator orchestrator)
        {
            _orchestrator = orchestrator;
        }

        [KernelFunction("llegeix_document_sencer")]
        [Description("Llegeix el contingut complet d'un document del disc. " +
                     "Usa aquesta tool NOMÉS com a últim recurs.")]
        public async Task<string> LlegeixDocumentSencerAsync(
            [Description("La pregunta de l'usuari")]
        string pregunta)
        {
            return await _orchestrator.GetFullDocumentAsync(pregunta);
        }
    }
}
