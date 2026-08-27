from transformers import DistilBertTokenizer, DistilBertForSequenceClassification, Trainer, TrainingArguments
from datasets import load_dataset
import pandas as pd
import os
import torch
import onnx
from transformers.onnx import export

# Load dataset
data = pd.read_csv("data/intents.csv")
dataset = load_dataset("csv", data_files={"train": "data/intents.csv"})["train"]

# Initialize tokenizer and model
tokenizer = DistilBertTokenizer.from_pretrained("distilbert-base-uncased")
model = DistilBertForSequenceClassification.from_pretrained("distilbert-base-uncased", num_labels=7)  # 7 unique intents

# Tokenize dataset
def tokenize_function(examples):
    return tokenizer(examples["Query"], padding="max_length", truncation=True, max_length=128)

tokenized_dataset = dataset.map(tokenize_function, batched=True)

# Encode labels and rename to "label"
tokenized_dataset = tokenized_dataset.class_encode_column("Intent")
tokenized_dataset = tokenized_dataset.rename_column("Intent", "label")

# Set format for PyTorch
tokenized_dataset.set_format("torch", columns=["input_ids", "attention_mask", "label"])

# Training arguments
training_args = TrainingArguments(
    output_dir="./intent_model",
    num_train_epochs=3,
    per_device_train_batch_size=8,
    logging_dir="./logs",
    logging_steps=10,
    save_strategy="epoch",
    load_best_model_at_end=False,
)

# Define compute_metrics function (optional, for evaluation)
def compute_metrics(eval_pred):
    predictions, labels = eval_pred
    predictions = torch.argmax(torch.tensor(predictions), dim=1)
    return {"accuracy": (predictions == labels).mean().item()}

# Trainer
trainer = Trainer(
    model=model,
    args=training_args,
    train_dataset=tokenized_dataset,
    # Optional: Add eval_dataset if you split your data
    # compute_metrics=compute_metrics,  # Uncomment if using eval
)

# Train and save
trainer.train()
model.save_pretrained("./intent_model")
tokenizer.save_pretrained("./intent_model")

# Export to ONNX with higher opset
dummy_input = tokenizer("sample input", return_tensors="pt", padding=True, truncation=True, max_length=128)
output_path = "./intent_model/model.onnx"
torch.onnx.export(trainer.model, dummy_input["input_ids"], output_path, opset_version=18, input_names=["input_ids"], output_names=["logits"], dynamic_axes={"input_ids": {0: "batch_size"}})