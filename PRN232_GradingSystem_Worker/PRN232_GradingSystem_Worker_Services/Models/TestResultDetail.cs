using System.Collections.Generic;

namespace PRN232_GradingSystem_Worker_Services.Models
{
    public class TestResultDetail
    {
        public double Q1Login { get; set; }
        public string Q1LoginNote { get; set; } = string.Empty;

        // Q2
        public double Q2ListAll { get; set; }
        public string Q2ListAllNote { get; set; } = string.Empty;
        public double Q2ListAll2 { get; set; }
        public string Q2ListAll2Note { get; set; } = string.Empty;
        public double Q2Pagging { get; set; }
        public string Q2PaggingNote { get; set; } = string.Empty;

        // Q3
        public double Q3AddOk { get; set; }
        public string Q3AddOkNote { get; set; } = string.Empty;
        public double Q3DisplayTop { get; set; }
        public string Q3DisplayTopNote { get; set; } = string.Empty;
        public double Q3ValidationCombobox { get; set; }
        public string Q3ValidationComboboxNote { get; set; } = string.Empty;
        public double Q3ValidationRequired { get; set; }
        public string Q3ValidationRequiredNote { get; set; } = string.Empty;
        public double Q3ValidationLength { get; set; }
        public string Q3ValidationLengthNote { get; set; } = string.Empty;
        public double Q3ValidationSpecialCharacters { get; set; }
        public string Q3ValidationSpecialCharactersNote { get; set; } = string.Empty;

        // Q4
        public double Q4UpdateOk { get; set; }
        public string Q4UpdateOkNote { get; set; } = string.Empty;
        public double Q4UpdateValidation { get; set; }
        public string Q4UpdateValidationNote { get; set; } = string.Empty;

        // Q5
        public double Q5Test1 { get; set; }
        public string Q5Test1Note { get; set; } = string.Empty;
        public double Q5Test2 { get; set; }
        public string Q5Test2Note { get; set; } = string.Empty;
        public double Q5Test3 { get; set; }
        public string Q5Test3Note { get; set; } = string.Empty;

        // Q6
        public double Q6DeleteWithSignalR { get; set; }
        public string Q6DeleteWithSignalRNote { get; set; } = string.Empty;
    }
}
