import { type ReactNode, useEffect } from 'react';
import { useForm, type DefaultValues, type FieldValues, type Path } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { toast } from 'sonner';
import type { ZodType } from 'zod';
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
  DialogFooter,
} from '@/components/ui/dialog';
import {
  Form,
  FormField,
  FormItem,
  FormLabel,
  FormControl,
  FormMessage,
} from '@/components/ui/form';
import { Input } from '@/components/ui/input';
import { Button } from '@/components/ui/button';

type Mode = 'create' | 'edit';

export interface FormDialogField<T extends FieldValues> {
  name: Path<T>;
  label: string;
  type?: 'text' | 'number';
  placeholder?: string;
  min?: number;
  className?: string;
  /** If true, this field is disabled in edit mode. */
  disabledOnEdit?: boolean;
  /** If true, this field gets autofocus in create mode. */
  autoFocusOnCreate?: boolean;
  /** If true, this field gets autofocus in edit mode. */
  autoFocusOnEdit?: boolean;
}

export interface FormDialogProps<T extends FieldValues> {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  mode: Mode;
  title: { create: string; edit: string };
  description: ReactNode;
  schema: ZodType<T>;
  fields: FormDialogField<T>[];
  defaultValues: DefaultValues<T>;
  initial?: T;
  existingNames?: Set<string>;
  nameField?: Path<T>;
  duplicateMessage?: string;
  successMessage: { create: string; edit: string };
  onSubmit: (values: T) => Promise<void>;
}

export function FormDialog<T extends FieldValues>({
  open,
  onOpenChange,
  mode,
  title,
  description,
  schema,
  fields,
  defaultValues,
  initial,
  existingNames,
  nameField,
  duplicateMessage = 'An entry with that name already exists',
  successMessage,
  onSubmit,
}: FormDialogProps<T>) {
  const form = useForm<T>({
    resolver: zodResolver(schema),
    defaultValues: (initial ?? defaultValues) as DefaultValues<T>,
  });

  useEffect(() => {
    if (open) {
      form.reset((initial ?? defaultValues) as DefaultValues<T>);
    }
  }, [open, initial, form, defaultValues]);

  const handleSubmit = form.handleSubmit(async (values) => {
    if (mode === 'create' && nameField && existingNames) {
      const trimmed = (String((values as Record<string, unknown>)[nameField]) ?? '').trim();
      if (existingNames.has(trimmed)) {
        form.setError(nameField, { message: duplicateMessage });
        return;
      }
    }
    try {
      await onSubmit(values);
      toast.success(mode === 'create' ? successMessage.create : successMessage.edit);
      onOpenChange(false);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Failed to save';
      toast.error(message);
    }
  });

  const isSubmitting = form.formState.isSubmitting;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{mode === 'create' ? title.create : title.edit}</DialogTitle>
          <DialogDescription>{description}</DialogDescription>
        </DialogHeader>
        <Form {...form}>
          <form onSubmit={handleSubmit} className="space-y-4">
            {fields.map((f) => (
              <FormField
                key={String(f.name)}
                control={form.control}
                name={f.name}
                render={({ field }) =>
                  f.type === 'number' ? (
                    <FormItem>
                      <FormLabel>{f.label}</FormLabel>
                      <FormControl>
                        <Input
                          type="number"
                          min={f.min}
                          value={(field.value as number) ?? ''}
                          onChange={(e) => field.onChange(e.target.value === '' ? NaN : Number(e.target.value))}
                          onBlur={field.onBlur}
                          name={field.name}
                          ref={field.ref}
                          autoFocus={mode === 'edit' ? f.autoFocusOnEdit : f.autoFocusOnCreate}
                        />
                      </FormControl>
                      <FormMessage />
                    </FormItem>
                  ) : (
                    <FormItem>
                      <FormLabel>{f.label}</FormLabel>
                      <FormControl>
                        <Input
                          {...field}
                          placeholder={f.placeholder}
                          className={f.className}
                          disabled={f.disabledOnEdit && mode === 'edit'}
                          autoFocus={mode === 'create' ? f.autoFocusOnCreate : f.autoFocusOnEdit}
                        />
                      </FormControl>
                      <FormMessage />
                    </FormItem>
                  )
                }
              />
            ))}
            <DialogFooter>
              <Button
                type="button"
                variant="ghost"
                onClick={() => onOpenChange(false)}
                disabled={isSubmitting}
              >
                Cancel
              </Button>
              <Button type="submit" disabled={isSubmitting}>
                {isSubmitting ? 'Saving...' : 'Save'}
              </Button>
            </DialogFooter>
          </form>
        </Form>
      </DialogContent>
    </Dialog>
  );
}
