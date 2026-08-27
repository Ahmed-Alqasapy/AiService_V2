using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AiService.Models
{
    [Table("EMBEDDINGS")]
    public class Embedding
    {
        [Key]
        [Column("EMBEDDING_ID", TypeName = "NUMBER")]
        public decimal EmbeddingId { get; set; }

        [Column("ENTITY_TYPE")]
        [StringLength(50)]
        [Unicode(false)]
        public string EntityType { get; set; } // "QuestionnaireDatum", "QuestionnaireAllLicn", "QuestionnaireLicOwner"

        [Column("ENTITY_ID", TypeName = "NUMBER")]
        public decimal? EntityId { get; set; } // Links to QuestionnaireDataRecid, LicnId, or ReqOwnId

        [Column("EMBEDDING_DATA", TypeName = "BLOB")]
        public byte[] EmbeddingData { get; set; } // Stores the embedding as a byte array

        [Column("CREATED_ON", TypeName = "DATE")]
        public DateTime CreatedOn { get; set; }

        [Column("CREATED_BY")]
        [StringLength(200)]
        [Unicode(false)]
        public string CreatedBy { get; set; }
    }
}
