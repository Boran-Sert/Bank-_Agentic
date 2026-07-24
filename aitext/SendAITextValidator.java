package com.turkiyefinans.actions.atmbranch.assistant.aitext;

import org.springframework.validation.Errors;
import org.springframework.validation.Validator;

public class SendAITextValidator implements Validator {

    public boolean supports(Class<?> aClass) {
        return SendAITextRequest.class.equals(aClass);
    }

    public void validate(Object o, Errors errors) {
    }
}
