# load_llama.py
import sys
import json
import torch

# ============ FIX ENCODING ISSUE ON WINDOWS ============
# Force UTF-8 encoding for stdout and stderr
if sys.platform.startswith("win"):
    import io
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding='utf-8')

from transformers import AutoTokenizer, AutoModelForCausalLM


# Hardcoded local model path (update to your exact snapshot path)
MODEL_PATH = r"C:\Users\LENOVO\.cache\huggingface\hub\models--meta-llama--Llama-3.2-1B-Instruct\snapshots\9213176726f574b556790deb65791e0c5aa438b6"

# Load model and tokenizer once
print("Loading model...", file=sys.stderr)
tokenizer = AutoTokenizer.from_pretrained(MODEL_PATH, local_files_only=True)
model = AutoModelForCausalLM.from_pretrained(
    MODEL_PATH,
    local_files_only=True,
    torch_dtype=torch.bfloat16,
    device_map="auto"
)
print("Model loaded.", file=sys.stderr)

def generate_response(final_prompt: str, options: dict):
    system_prompt = """أنت أداة استخراج نصوص قانونية دقيقة من الوثائق الرسمية الكويتية.
                    مهمتك الوحيدة: تقديم الإجابة بناءً على الوثائق المرفقة فقط، مع الالتزام التام بالقواعد التالية:

                    قواعد الإجابة:
                    1. استخدم فقط المعلومات الموجودة في الوثائق المرفقة.
                    2. انسخ النصوص المهمة حرفيًا من الوثائق مع الحفاظ على التنسيق الأصلي (مثل: •، ▪، الترقيم، المسافات، العناوين).
                    3. إذا كانت الإجابة موزعة على عدة أقسام، اجمعها بترتيب منطقي مع الحفاظ على العناوين والأرقام.
                    4. احتفظ بالعناوين والأقسام كما هي (مثل: #، ##، -).
                    5. لا تضيف أي معلومات خارجية، لا تفسر، لا تلخص، لا تعلق، لا تكتب مقدمات أو خاتمات.
                    6. لا تكتب عبارات مثل "بالتأكيد" أو "الإجابة هي" أو "وفقًا للوثائق" أو "انتهت المقالة".
                    7. ابدأ الإجابة مباشرة بعد علامة "## الإجابة:" دون أي كلمة إضافية.

                    أجب دائمًا بنسخ حرفي ومنسق من الوثائق، ولا شيء غير ذلك."""

    messages = [
        {"role": "system", "content": system_prompt},
        {"role": "user", "content": final_prompt}
    ]

    inputs = tokenizer.apply_chat_template(
        messages,
        add_generation_prompt=True,
        return_dict=True,
        return_tensors="pt"
    ).to(model.device)

    gen_options = {
    "max_new_tokens": 1024,
    "temperature": 0.2,        # Low for accuracy in RAG
    "top_p": 0.95,
    "do_sample": True,
    "repetition_penalty": 1.1,
    "stopping_criteria": None   # We'll handle manually if needed
        }
    gen_options.update(options)

    with torch.no_grad():
        outputs = model.generate(
            **inputs,
            **gen_options,
            pad_token_id=tokenizer.eos_token_id
        )

    response = tokenizer.decode(outputs[0][inputs["input_ids"].shape[-1]:], skip_special_tokens=True)
    return response.strip()

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(json.dumps({"error": "No prompt provided"}))
        sys.exit(1)

    try:
        # argv[1] = finalPrompt (string)
        # argv[2] = llmOptions JSON (optional)
        final_prompt = sys.argv[1]
        options_json = sys.argv[2] if len(sys.argv) > 2 else "{}"
        options = json.loads(options_json)

        response = generate_response(final_prompt, options)
        print(response)  # This goes to stdout → C# will read it

    except Exception as e:
        print(json.dumps({"error": str(e)}))
        sys.exit(1)